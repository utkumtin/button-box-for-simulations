using System.Diagnostics;
using ButtonBox.Core.Contracts;
using ButtonBox.Output.Scs.Install;
using Xunit;

namespace ButtonBox.Output.Scs.Tests.Install;

/// <summary>A temporary folder tree. Junctions it created are removed as links (never through their target) before the tree is deleted.</summary>
internal sealed class TempRoot : IDisposable
{
    private readonly List<string> _junctions = [];

    public TempRoot()
    {
        Path = Directory.CreateTempSubdirectory("bbfs-install-").FullName;
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string MakeDir(params string[] parts)
    {
        var dir = Combine(parts);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string WriteFile(string relative, string content)
    {
        var file = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }

    public string WriteBytes(string relative, byte[] content)
    {
        var file = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, content);
        return file;
    }

    /// <summary>Creates a directory junction (no admin needed) from <paramref name="link"/> to <paramref name="target"/>.</summary>
    public string Junction(string link, string target)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(link)!);
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
        _junctions.Add(link);
        return link;
    }

    public void Dispose()
    {
        for (var i = _junctions.Count - 1; i >= 0; i--)
        {
            if (Directory.Exists(_junctions[i]))
            {
                Directory.Delete(_junctions[i]); // removes the junction only, never its target
            }
        }

        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

/// <summary>A fake Steam installation in a temp folder: a root, libraries, manifests and game folders with an empty executable.</summary>
internal sealed class SteamFixture : IDisposable
{
    public SteamFixture()
    {
        Temp = new TempRoot();
        SteamRoot = Temp.MakeDir("Steam");
        Directory.CreateDirectory(System.IO.Path.Combine(SteamRoot, "steamapps"));
    }

    public TempRoot Temp { get; }

    public string SteamRoot { get; }

    /// <summary>A library folder (with its <c>steamapps</c> folder); not yet listed in <c>libraryfolders.vdf</c>.</summary>
    public string AddLibrary(string name)
    {
        var library = Temp.MakeDir(name);
        Directory.CreateDirectory(System.IO.Path.Combine(library, "steamapps", "common"));
        return library;
    }

    /// <summary>Writes the manifest and, when asked, the game folder with its executable. Returns the game folder.</summary>
    public string AddGame(string library, ScsGame game, string installDir, bool exe = true, bool manifest = true)
    {
        var gameDir = System.IO.Path.Combine(library, "steamapps", "common", installDir);
        if (exe)
        {
            var exePath = System.IO.Path.Combine(gameDir, "bin", "win_x64", ScsGameInstall.ExeName(game));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(exePath)!);
            File.WriteAllBytes(exePath, []);
        }

        if (manifest)
        {
            WriteManifest(library, game, $"\"AppState\"\n{{\n\t\"appid\"\t\"{ScsGameInstall.AppId(game)}\"\n\t\"installdir\"\t\"{installDir.Replace("\\", "\\\\")}\"\n}}\n");
        }

        return gameDir;
    }

    public void WriteManifest(string library, ScsGame game, string content)
    {
        var steamapps = System.IO.Path.Combine(library, "steamapps");
        Directory.CreateDirectory(steamapps);
        File.WriteAllText(System.IO.Path.Combine(steamapps, $"appmanifest_{ScsGameInstall.AppId(game)}.acf"), content);
    }

    /// <summary>Writes <c>libraryfolders.vdf</c> in the shape Steam writes today (paths with escaped backslashes).</summary>
    public void WriteLibraryFolders(params string[] libraryPaths) => WriteLibraryFoldersRaw(libraryPaths.Select(p => p.Replace("\\", "\\\\")).ToArray());

    /// <summary>Like <see cref="WriteLibraryFolders"/> but the path text is written as given.</summary>
    public void WriteLibraryFoldersRaw(params string[] pathTexts)
    {
        var body = string.Concat(pathTexts.Select((p, i) => $"\t\"{i}\"\n\t{{\n\t\t\"path\"\t\t\"{p}\"\n\t\t\"label\"\t\t\"\"\n\t}}\n"));
        File.WriteAllText(System.IO.Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\"\n{{\n{body}}}\n");
    }

    public SteamGameLocator Locator() => new(() => SteamRoot);

    public void Dispose() => Temp.Dispose();
}
