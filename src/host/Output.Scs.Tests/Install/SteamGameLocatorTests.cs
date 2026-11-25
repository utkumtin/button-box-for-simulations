using ButtonBox.Core.Contracts;
using ButtonBox.Output.Scs.Install;
using Xunit;

namespace ButtonBox.Output.Scs.Tests.Install;

// Why: the pipe server check trusts exactly the executables this locator returns, and the installer writes into the folders it
// returns (decisions §9, §12). Steam's files are user-writable, so a path from them must never point outside a Steam library's
// steamapps\common, and a normal setup (second library, library behind a junction) must keep working.
public class SteamGameLocatorTests
{
    private const string Ets2Dir = "Euro Truck Simulator 2";
    private const string AtsDir = "American Truck Simulator";

    private static string Final(string path) => FinalPath.Resolve(path)!;

    [Fact]
    public void Finds_both_games_in_two_libraries()
    {
        using var steam = new SteamFixture();
        var second = steam.AddLibrary("SteamLibrary");
        var ets2 = steam.AddGame(steam.SteamRoot, ScsGame.Ets2, Ets2Dir);
        var ats = steam.AddGame(second, ScsGame.Ats, AtsDir);
        steam.WriteLibraryFolders(steam.SteamRoot, second);

        var found = steam.Locator().Find();

        Assert.Equal(2, found.Count);
        Assert.Equal(Final(ets2), found.Single(g => g.Game == ScsGame.Ets2).InstallDir);
        Assert.Equal(Final(ats), found.Single(g => g.Game == ScsGame.Ats).InstallDir);
        Assert.EndsWith(@"bin\win_x64\eurotrucks2.exe", found.Single(g => g.Game == ScsGame.Ets2).ExePath);
    }

    // Why: a user with one library has no libraryfolders entry for the Steam root on some versions.
    [Fact]
    public void Finds_a_game_in_the_steam_root_that_is_not_listed()
    {
        using var steam = new SteamFixture();
        var ets2 = steam.AddGame(steam.SteamRoot, ScsGame.Ets2, Ets2Dir);
        steam.WriteLibraryFolders();

        Assert.Equal(Final(ets2), Assert.Single(steam.Locator().Find()).InstallDir);
    }

    [Fact]
    public void Finds_a_game_in_the_steam_root_without_a_library_folders_file()
    {
        using var steam = new SteamFixture();
        steam.AddGame(steam.SteamRoot, ScsGame.Ats, AtsDir);

        Assert.Equal(ScsGame.Ats, Assert.Single(steam.Locator().Find()).Game);
    }

    [Fact]
    public void The_first_library_that_has_the_game_wins()
    {
        using var steam = new SteamFixture();
        var second = steam.AddLibrary("Lib2");
        var third = steam.AddLibrary("Lib3");
        var inSecond = steam.AddGame(second, ScsGame.Ets2, Ets2Dir);
        steam.AddGame(third, ScsGame.Ets2, Ets2Dir);
        steam.WriteLibraryFolders(second, third);

        Assert.Equal(Final(inSecond), Assert.Single(steam.Locator().Find()).InstallDir);
    }

    [Fact]
    public void A_manifest_without_the_game_folder_or_exe_is_not_found()
    {
        using var steam = new SteamFixture();
        steam.AddGame(steam.SteamRoot, ScsGame.Ets2, Ets2Dir, exe: false);
        steam.AddGame(steam.SteamRoot, ScsGame.Ats, AtsDir, exe: false);
        Directory.CreateDirectory(Path.Combine(steam.SteamRoot, "steamapps", "common", AtsDir, "bin", "win_x64"));
        steam.WriteLibraryFolders(steam.SteamRoot);

        Assert.Empty(steam.Locator().Find());
    }

    // Why: the executable is what the pipe check trusts; the other game's exe must not count for this game.
    [Fact]
    public void The_other_games_exe_does_not_count()
    {
        using var steam = new SteamFixture();
        var dir = steam.AddGame(steam.SteamRoot, ScsGame.Ets2, Ets2Dir, exe: false);
        Directory.CreateDirectory(Path.Combine(dir, "bin", "win_x64"));
        File.WriteAllBytes(Path.Combine(dir, "bin", "win_x64", "amtrucks.exe"), []);

        Assert.Empty(steam.Locator().Find());
    }

    [Fact]
    public void A_folder_named_like_the_exe_does_not_count()
    {
        using var steam = new SteamFixture();
        var dir = steam.AddGame(steam.SteamRoot, ScsGame.Ets2, Ets2Dir, exe: false);
        Directory.CreateDirectory(Path.Combine(dir, "bin", "win_x64", "eurotrucks2.exe"));

        Assert.Empty(steam.Locator().Find());
    }

    [Fact]
    public void A_game_without_a_manifest_is_not_found()
    {
        using var steam = new SteamFixture();
        steam.AddGame(steam.SteamRoot, ScsGame.Ets2, Ets2Dir, manifest: false);

        Assert.Empty(steam.Locator().Find());
    }

    [Fact]
    public void A_manifest_without_installdir_is_not_found()
    {
        using var steam = new SteamFixture();
        steam.AddGame(steam.SteamRoot, ScsGame.Ets2, Ets2Dir, manifest: false);
        steam.WriteManifest(steam.SteamRoot, ScsGame.Ets2, "\"AppState\" { \"appid\" \"227300\" }");

        Assert.Empty(steam.Locator().Find());
    }

    [Fact]
    public void A_manifest_for_another_app_is_not_this_game()
    {
        using var steam = new SteamFixture();
        steam.AddGame(steam.SteamRoot, ScsGame.Ets2, Ets2Dir, manifest: false);
        steam.WriteManifest(steam.SteamRoot, ScsGame.Ets2, $"\"AppState\" {{ \"appid\" \"270880\" \"installdir\" \"{Ets2Dir}\" }}");

        Assert.Empty(steam.Locator().Find());
    }

    [Fact]
    public void A_broken_manifest_or_library_file_is_not_found_and_does_not_throw()
    {
        using var steam = new SteamFixture();
        steam.AddGame(steam.SteamRoot, ScsGame.Ets2, Ets2Dir);
        steam.WriteManifest(steam.SteamRoot, ScsGame.Ets2, "\"AppState\" { \"installdir\" \"unterminated");
        File.WriteAllText(Path.Combine(steam.SteamRoot, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" {");

        Assert.Empty(steam.Locator().Find());
    }

    [Fact]
    public void An_oversized_manifest_or_library_file_is_ignored()
    {
        using var steam = new SteamFixture();
        var other = steam.AddLibrary("Other");
        steam.AddGame(other, ScsGame.Ets2, Ets2Dir);
        steam.AddGame(steam.SteamRoot, ScsGame.Ats, AtsDir, manifest: false);
        var padding = new string(' ', (int)SteamGameLocator.MaxManifestBytes);
        steam.WriteManifest(steam.SteamRoot, ScsGame.Ats, $"\"AppState\" {{ \"installdir\" \"{AtsDir}\" }}{padding}");
        var vdfPadding = new string(' ', (int)SteamGameLocator.MaxLibraryFoldersBytes);
        steam.WriteLibraryFolders(other);
        File.AppendAllText(Path.Combine(steam.SteamRoot, "steamapps", "libraryfolders.vdf"), vdfPadding);

        // The oversized library list is ignored (so "Other" is unknown) and the oversized manifest is ignored.
        Assert.Empty(steam.Locator().Find());
    }

    // Why: installdir comes from a user-writable file. Each of these would make the "game folder" something outside
    // steamapps\common and so let an arbitrary executable be trusted or an arbitrary folder written to.
    [Theory]
    [InlineData(@"..\..\Windows")]
    [InlineData(@"..")]
    [InlineData(@".")]
    [InlineData(@"a\b")]
    [InlineData(@"a/b")]
    [InlineData(@"C:\x")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\rooted")]
    [InlineData(@"name.")]
    [InlineData(@"name ")]
    [InlineData(@" name")]
    [InlineData(@"na:me")]
    [InlineData(@"na*me")]
    [InlineData(@"CON")]
    [InlineData(@"nul.txt")]
    public void A_hostile_installdir_is_refused(string installDir)
    {
        using var steam = new SteamFixture();
        // A real game, reachable by the hostile name if it were accepted: the folder two levels up from common.
        steam.AddGame(steam.SteamRoot, ScsGame.Ets2, Ets2Dir, manifest: false);
        steam.WriteManifest(steam.SteamRoot, ScsGame.Ets2, $"\"AppState\" {{ \"appid\" \"227300\" \"installdir\" \"{installDir.Replace("\\", "\\\\")}\" }}");

        Assert.Empty(steam.Locator().Find());
    }

    // The one case that would really have escaped: ..\ out of common into a folder holding the exe.
    [Fact]
    public void A_dotdot_installdir_cannot_reach_a_folder_outside_common()
    {
        using var steam = new SteamFixture();
        var outside = steam.Temp.MakeDir("Outside", "bin", "win_x64");
        File.WriteAllBytes(Path.Combine(outside, "eurotrucks2.exe"), []);
        steam.WriteManifest(steam.SteamRoot, ScsGame.Ets2, "\"AppState\" { \"installdir\" \"..\\\\..\\\\..\\\\Outside\" }");

        Assert.Empty(steam.Locator().Find());
    }

    [Fact]
    public void A_name_with_a_dot_inside_is_a_plain_name()
    {
        using var steam = new SteamFixture();
        steam.AddGame(steam.SteamRoot, ScsGame.Ets2, "Euro Truck.Simulator 2");

        Assert.Single(steam.Locator().Find());
    }

    [Theory]
    [InlineData(@"\\server\share\SteamLibrary")]
    [InlineData(@"\\?\C:\SteamLibrary")]
    [InlineData(@"\\.\C:\SteamLibrary")]
    [InlineData(@"\??\C:\SteamLibrary")]
    [InlineData(@"SteamLibrary")]
    [InlineData(@"..\SteamLibrary")]
    [InlineData(@"\SteamLibrary")]
    [InlineData(@"C:SteamLibrary")]
    [InlineData(@"C:")]
    [InlineData(@"")]
    [InlineData(@"C:\Steam*")]
    [InlineData(@"C:\Steam:stream")]
    [InlineData(@"1:\SteamLibrary")]
    public void A_library_path_that_is_not_an_absolute_local_drive_path_is_refused(string path)
    {
        Assert.Null(SteamGameLocator.NormalizeLibraryPath(path));
    }

    [Fact]
    public void A_library_on_a_drive_that_does_not_exist_is_refused()
    {
        var unused = Enumerable.Range('A', 26).Select(c => (char)c).First(c => !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c));
        Assert.Null(SteamGameLocator.NormalizeLibraryPath($@"{unused}:\SteamLibrary"));
    }

    [Fact]
    public void A_listed_hostile_library_is_skipped_and_the_others_are_still_read()
    {
        using var steam = new SteamFixture();
        var good = steam.AddLibrary("Good");
        var ets2 = steam.AddGame(good, ScsGame.Ets2, Ets2Dir);
        steam.WriteLibraryFoldersRaw(@"\\\\evil\\share\\Lib", @"\\\\?\\C:\\Lib", "relative\\\\Lib", Path.Combine(steam.Temp.Path, "Missing").Replace("\\", "\\\\"), good.Replace("\\", "\\\\"));

        Assert.Equal(Final(ets2), Assert.Single(steam.Locator().Find()).InstallDir);
    }

    [Fact]
    public void A_library_path_is_normalized()
    {
        using var steam = new SteamFixture();
        var library = steam.AddLibrary("Lib");
        var ets2 = steam.AddGame(library, ScsGame.Ets2, Ets2Dir);
        steam.WriteLibraryFolders(Path.Combine(library, "..", "Lib") + @"\");

        Assert.Equal(Final(ets2), Assert.Single(steam.Locator().Find()).InstallDir);
    }

    // Why: the registry holds the Steam path as "c:/program files (x86)/steam".
    [Fact]
    public void A_forward_slash_lower_case_steam_path_is_normalized()
    {
        using var steam = new SteamFixture();
        var ets2 = steam.AddGame(steam.SteamRoot, ScsGame.Ets2, Ets2Dir);
        var spelled = steam.SteamRoot.Replace('\\', '/').ToLowerInvariant();

        var locator = new SteamGameLocator(() => spelled);

        Assert.Equal(Final(ets2), Assert.Single(locator.Find()).InstallDir);
    }

    [Fact]
    public void Without_steam_nothing_is_found()
    {
        Assert.Empty(new SteamGameLocator(() => null).Find());
        Assert.Empty(new SteamGameLocator(() => string.Empty).Find());
        using var temp = new TempRoot();
        Assert.Empty(new SteamGameLocator(() => temp.Combine("NoSteam")).Find());
        Assert.Empty(new SteamGameLocator(() => @"\\server\share\Steam").Find());
    }

    // Why (measured on the user's PC): Windows reports a running process's image by its final path and the pipe server check
    // compares it with ExePath. A Steam library behind a junction is a normal setup and must be found, with the target as path.
    [Fact]
    public void A_library_behind_a_junction_is_found_and_reported_by_its_target()
    {
        using var steam = new SteamFixture();
        var real = steam.AddLibrary("RealLibrary");
        var ets2 = steam.AddGame(real, ScsGame.Ets2, Ets2Dir);
        var link = steam.Temp.Junction(steam.Temp.Combine("LinkedLibrary"), real);
        steam.WriteLibraryFolders(link);

        var found = Assert.Single(steam.Locator().Find());

        Assert.Equal(Final(ets2), found.InstallDir);
        Assert.DoesNotContain("LinkedLibrary", found.InstallDir, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(found.ExePath));
    }

    [Fact]
    public void A_game_folder_that_is_itself_a_junction_is_found_by_its_target()
    {
        using var steam = new SteamFixture();
        var library = steam.AddLibrary("Lib");
        var real = steam.Temp.MakeDir("MovedGames", Ets2Dir);
        Directory.CreateDirectory(Path.Combine(real, "bin", "win_x64"));
        File.WriteAllBytes(Path.Combine(real, "bin", "win_x64", "eurotrucks2.exe"), []);
        steam.Temp.Junction(Path.Combine(library, "steamapps", "common", Ets2Dir), real);
        steam.AddGame(library, ScsGame.Ets2, Ets2Dir, exe: false);
        steam.WriteLibraryFolders(library);

        Assert.Equal(Final(real), Assert.Single(steam.Locator().Find()).InstallDir);
    }

    [Fact]
    public void The_locator_reads_steam_again_on_every_call()
    {
        using var steam = new SteamFixture();
        var locator = steam.Locator();
        Assert.Empty(locator.Find());

        steam.AddGame(steam.SteamRoot, ScsGame.Ets2, Ets2Dir);

        Assert.Single(locator.Find());
    }
}
