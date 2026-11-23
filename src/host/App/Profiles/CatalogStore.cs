using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;

namespace ButtonBox.App.Profiles;

/// <summary>
/// The action catalogs that ship with the app (<c>catalog\&lt;game&gt;.json</c>, decisions §4). A game without a usable
/// catalog cannot be chosen. A file that is damaged, too large or whose game differs from its name makes only that game
/// unavailable and is recorded in <see cref="Problems"/> (file name and reason, never file content). Immutable after construction.
/// </summary>
public sealed class CatalogStore
{
    private const int MaxFileBytes = 256 * 1024;
    private const int SchemaVersion = 1;

    private readonly SortedDictionary<Game, Catalog> _byGame = [];
    private readonly List<string> _problems = [];

    public CatalogStore(string shippedDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(shippedDir);
        if (!Directory.Exists(shippedDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(shippedDir, "*.json").Order(StringComparer.Ordinal))
        {
            Load(file);
        }
    }

    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Sorted by game.</summary>
    public IReadOnlyList<Catalog> All => [.. _byGame.Values];

    public bool TryGet(Game game, [NotNullWhen(true)] out Catalog? catalog) => _byGame.TryGetValue(game, out catalog);

    /// <summary>The name of a game on the wire, as in the schema (<c>ets2</c>).</summary>
    public static string WireName(Game game) =>
        typeof(Game).GetField(game.ToString())?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
        ?? throw new ArgumentOutOfRangeException(nameof(game));

    private void Load(string file)
    {
        var name = Path.GetFileName(file);
        try
        {
            if (new FileInfo(file).Length > MaxFileBytes)
            {
                _problems.Add($"{name}: file is too large.");
                return;
            }

            var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllBytes(file), ContractJson.Options);
            if (!IsUsable(catalog))
            {
                _problems.Add($"{name}: not a valid catalog.");
                return;
            }

            if (!Enum.IsDefined(catalog.Game) || !string.Equals(Path.GetFileNameWithoutExtension(file), WireName(catalog.Game), StringComparison.Ordinal))
            {
                _problems.Add($"{name}: the game does not match the file name.");
                return;
            }

            if (!_byGame.TryAdd(catalog.Game, catalog))
            {
                _problems.Add($"{name}: duplicate catalog for the game.");
            }
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _problems.Add($"{name}: cannot be read.");
        }
    }

    private static bool IsUsable([NotNullWhen(true)] Catalog? catalog)
    {
        if (catalog is not { SchemaVersion: SchemaVersion, Actions: not null } || catalog.Actions.Any(a => a is not { Id: not null, Name: not null, Output: not null }))
        {
            return false;
        }

        // With no layouts, the profile check only reports catalog-level faults: duplicate ids and outputs of the wrong kind.
        if (!Enum.IsDefined(catalog.Game))
        {
            return false;
        }

        var empty = new Profile { SchemaVersion = SchemaVersion, Id = "catalog-check", Name = "catalog-check", Game = catalog.Game, Layouts = [] };
        return ProfileCheck.Problems(empty, catalog).Count == 0;
    }
}
