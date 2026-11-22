using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;

namespace ButtonBox.App.Pairing;

/// <summary>
/// Device templates: the ones shipped with the app (read-only, <c>templates/*.json</c> next to the executable) and the
/// generic ones generated from a phone's reported screen (persisted in the data directory). Thread-safe.
/// Every file is untrusted: it must parse with <see cref="ContractJson.Options"/> and pass <see cref="ContractValidator"/>.
/// </summary>
public sealed class TemplateStore
{
    private const int MaxFileBytes = 16 * 1024;
    private const string GenericPrefix = "generic-";

    private readonly string _genericDir;
    private readonly object _gate = new();
    private readonly SortedDictionary<string, DeviceTemplate> _shipped = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, DeviceTemplate> _generic = new(StringComparer.Ordinal);
    private readonly List<string> _problems = [];

    public TemplateStore(string shippedDir, string genericDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(shippedDir);
        ArgumentException.ThrowIfNullOrEmpty(genericDir);
        _genericDir = genericDir;
        LoadDirectory(shippedDir, _shipped, generic: false);
        LoadDirectory(genericDir, _generic, generic: true);
        foreach (var id in _generic.Keys.Where(_shipped.ContainsKey).ToList())
        {
            // Shipped wins; the generic file stays on disk but is never offered.
            _generic.Remove(id);
            _problems.Add($"Generic template '{id}' has the id of a shipped template and was ignored.");
        }
    }

    /// <summary>One line per file that was skipped (file name and reason, never file content).</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_gate)
            {
                return [.. _problems];
            }
        }
    }

    /// <summary>Shipped templates first, then generic ones, each sorted by id.</summary>
    public IReadOnlyList<DeviceTemplate> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _shipped.Values, .. _generic.Values];
            }
        }
    }

    public bool TryGet(string? id, [NotNullWhen(true)] out DeviceTemplate? template)
    {
        template = null;
        if (id is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _shipped.TryGetValue(id, out template) || _generic.TryGetValue(id, out template);
        }
    }

    /// <summary>
    /// The generic template for exactly this screen, created and persisted on first use. The same screen always yields the
    /// same id (a hash over every field), a screen differing only in one inset yields another.
    /// </summary>
    public DeviceTemplate GetOrAddGeneric(ScreenInfo screen)
    {
        if (!ContractValidator.IsValidScreen(screen))
        {
            throw new ArgumentException("Invalid screen.", nameof(screen));
        }

        var id = GenericId(screen);
        lock (_gate)
        {
            if (_generic.TryGetValue(id, out var existing))
            {
                return existing;
            }

            var template = new DeviceTemplate
            {
                SchemaVersion = ContractValidator.TemplateSchemaVersion,
                Id = id,
                Name = string.Create(CultureInfo.InvariantCulture, $"Generic {screen.Width}×{screen.Height}"),
                Screen = screen,
                Cutout = Cutout.Unknown,
                Generic = true,
            };
            AtomicFile.WriteAllBytes(Path.Combine(_genericDir, id + ".json"), JsonSerializer.SerializeToUtf8Bytes(template, ContractJson.Options));
            _generic[id] = template;
            return template;
        }
    }

    /// <summary>A chosen template mismatches the phone when width, height or pixel ratio differ. Safe-area insets are ignored.</summary>
    public static bool Mismatches(DeviceTemplate template, ScreenInfo reported) =>
        template.Screen.Width != reported.Width
        || template.Screen.Height != reported.Height
        || template.Screen.PixelRatio != reported.PixelRatio;

    public static string GenericId(ScreenInfo screen)
    {
        var inv = CultureInfo.InvariantCulture;
        var canonical = string.Create(inv,
            $"{screen.Width}|{screen.Height}|{screen.SafeArea.Top.ToString("R", inv)}|{screen.SafeArea.Right.ToString("R", inv)}|{screen.SafeArea.Bottom.ToString("R", inv)}|{screen.SafeArea.Left.ToString("R", inv)}|{screen.PixelRatio.ToString("R", inv)}");
        return GenericPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12];
    }

    private void LoadDirectory(string dir, SortedDictionary<string, DeviceTemplate> target, bool generic)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileBytes)
                {
                    _problems.Add($"{name}: file is too large.");
                    continue;
                }

                var template = JsonSerializer.Deserialize<DeviceTemplate>(File.ReadAllBytes(file), ContractJson.Options);
                if (!ContractValidator.IsValid(template))
                {
                    _problems.Add($"{name}: not a valid device template.");
                    continue;
                }

                if (template!.Generic != generic)
                {
                    _problems.Add($"{name}: the generic flag does not match the folder.");
                    continue;
                }

                if (!target.TryAdd(template.Id, template))
                {
                    _problems.Add($"{name}: duplicate template id.");
                }
            }
            catch (Exception e) when (e is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                _problems.Add($"{name}: cannot be read.");
            }
        }
    }
}
