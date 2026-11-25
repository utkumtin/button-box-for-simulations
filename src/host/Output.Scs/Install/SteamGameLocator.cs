using System.Globalization;
using System.Security;
using ButtonBox.Core.Contracts;
using Microsoft.Win32;

namespace ButtonBox.Output.Scs.Install;

/// <summary>
/// Finds ETS2 and ATS through Steam (decisions §12): the Steam path, the libraries in <c>steamapps\libraryfolders.vdf</c> and the
/// game folder named by <c>steamapps\appmanifest_&lt;appid&gt;.acf</c>. Both Steam files are user-writable, so every path taken from
/// them is checked strictly: the pipe server check trusts the executables found here (decisions §9).
/// </summary>
public sealed class SteamGameLocator : IScsGameLocator
{
    internal const long MaxLibraryFoldersBytes = 1024 * 1024;
    internal const long MaxManifestBytes = 256 * 1024;
    private const int MaxPathLength = 1024;
    private const int MaxInstallDirLength = 128;

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private readonly Func<string?> _steamPath;

    /// <summary>Reads the Steam path from <c>HKCU\Software\Valve\Steam\SteamPath</c> (read only).</summary>
    public SteamGameLocator()
        : this(ReadSteamPathFromRegistry)
    {
    }

    /// <param name="steamPath">The Steam folder; tests inject a temporary folder. Called on every <see cref="Find"/>.</param>
    public SteamGameLocator(Func<string?> steamPath)
    {
        ArgumentNullException.ThrowIfNull(steamPath);
        _steamPath = steamPath;
    }

    public IReadOnlyList<ScsGameInstall> Find()
    {
        var found = new List<ScsGameInstall>();
        try
        {
            var libraries = Libraries();
            foreach (var game in new[] { ScsGame.Ets2, ScsGame.Ats })
            {
                foreach (var library in libraries)
                {
                    var install = TryFindIn(library, game);
                    if (install is not null)
                    {
                        found.Add(install);
                        break;
                    }
                }
            }
        }
        catch (Exception e) when (IsExpected(e))
        {
            // A broken Steam setup means "not found", never an error for the caller.
        }

        return found;
    }

    /// <summary>
    /// A library path from Steam: an absolute path with a drive letter on a local (fixed or removable) drive. Rejects UNC, device
    /// and extended-length paths (prefix backslash backslash question mark or dot), relative and drive-relative paths and anything
    /// with wildcards or alternate data streams. Forward slashes are accepted (the registry value uses them). Returns the full
    /// path, or null.
    /// </summary>
    internal static string? NormalizeLibraryPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxPathLength || raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        var path = raw.Replace('/', '\\');
        if (path.Length < 3 || !IsDriveLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.IndexOfAny(['*', '?']) >= 0 || path.LastIndexOf(':') != 1)
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }

        return IsLocalDrivePath(full) ? TrimTrailingSeparator(full) : null;
    }

    /// <summary>The <c>installdir</c> of a manifest: one plain folder name. Anything that could leave <c>steamapps\common</c> is refused.</summary>
    internal static bool IsPlainFolderName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxInstallDirLength || name == "." || name == "..")
        {
            return false;
        }

        if (name[0] == ' ' || name[^1] is ' ' or '.')
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c < ' ' || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                return false;
            }
        }

        var stem = name.Split('.')[0].TrimEnd(' ');
        return !ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    private static string? ReadSteamPathFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: false);
            return key?.GetValue("SteamPath") as string;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private List<string> Libraries()
    {
        var libraries = new List<string>();
        var root = NormalizeLibraryPath(_steamPath());
        if (root is null)
        {
            return libraries;
        }

        var vdf = Vdf.TryReadFile(Path.Combine(root, "steamapps", "libraryfolders.vdf"), MaxLibraryFoldersBytes);
        if (vdf?.GetObject("libraryfolders") is { } folders)
        {
            foreach (var (_, value) in folders.Entries)
            {
                if (value is VdfObject entry && NormalizeLibraryPath(entry.GetString("path")) is { } path && !libraries.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    libraries.Add(path);
                }
            }
        }

        if (!libraries.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            libraries.Add(root);
        }

        return libraries;
    }

    private static ScsGameInstall? TryFindIn(string library, ScsGame game)
    {
        try
        {
            var appId = ScsGameInstall.AppId(game).ToString(CultureInfo.InvariantCulture);
            var manifest = Vdf.TryReadFile(Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf"), MaxManifestBytes);
            var state = manifest?.GetObject("AppState");
            var installDir = state?.GetString("installdir");
            if (!IsPlainFolderName(installDir))
            {
                return null;
            }

            // A manifest that names another app is not this game's manifest.
            if (state!.GetString("appid") is { } declared && declared != appId)
            {
                return null;
            }

            var common = Path.Combine(library, "steamapps", "common");
            var gameDir = Path.GetFullPath(Path.Combine(common, installDir!));
            if (!string.Equals(Path.GetDirectoryName(gameDir), common, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(gameDir))
            {
                return null;
            }

            // The final path (junctions, links and subst drives resolved) is what Windows reports as a running process's image.
            var final = FinalPath.Resolve(gameDir);
            if (final is null || !IsLocalDrivePath(final))
            {
                return null;
            }

            final = TrimTrailingSeparator(final);
            var exe = Path.Combine(final, "bin", "win_x64", ScsGameInstall.ExeName(game));
            return File.Exists(exe) ? new ScsGameInstall(game, final) : null;
        }
        catch (Exception e) when (IsExpected(e))
        {
            return null;
        }
    }

    private static bool IsLocalDrivePath(string full)
    {
        if (full.Length < 3 || !IsDriveLetter(full[0]) || full[1] != ':' || full[2] != '\\')
        {
            return false;
        }

        try
        {
            return new DriveInfo(full[..1]).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsDriveLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static string TrimTrailingSeparator(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    private static bool IsExpected(Exception e) =>
        e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException or InvalidOperationException;
}
