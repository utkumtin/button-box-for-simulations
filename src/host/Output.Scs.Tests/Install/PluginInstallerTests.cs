using System.Diagnostics;
using System.Runtime.InteropServices;
using ButtonBox.Core.Contracts;
using ButtonBox.Output.Scs.Install;
using Xunit;

namespace ButtonBox.Output.Scs.Tests.Install;

// Why: the installer writes into a game folder with the user's rights, from a path that came out of user-writable Steam files
// (decisions §12). It must never replace anything but its own DLL, never follow a link out of the game folder, never leave a
// half-written or temporary file behind, and must say "close the game" instead of failing halfway when the DLL is loaded.
public class PluginInstallerTests : IDisposable
{
    private static readonly byte[] ShippedBytes = [.. "shipped plugin v2"u8];
    private static readonly byte[] OldBytes = [.. "older plugin v1"u8];

    private readonly SteamFixture _steam = new();
    private readonly string _gameDir;
    private readonly string _pluginDir;
    private readonly string _shipped;

    public PluginInstallerTests()
    {
        _gameDir = _steam.AddGame(_steam.SteamRoot, ScsGame.Ets2, "Euro Truck Simulator 2");
        _gameDir = FinalPath.Resolve(_gameDir)!;
        _pluginDir = Path.Combine(_gameDir, "bin", "win_x64", "plugins");
        _shipped = _steam.Temp.WriteBytes(@"app\button_box_scs.dll", ShippedBytes);
    }

    private string Target => Path.Combine(_pluginDir, PluginInstaller.DllName);

    public void Dispose() => _steam.Dispose();

    private PluginInstaller Installer(Func<string, bool>? isRunning = null) =>
        new(_steam.Locator(), _shipped, isRunning ?? (_ => false));

    private static string[] TempFiles(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "*.tmp") : [];

    [Fact]
    public void Not_installed_then_installed_then_up_to_date()
    {
        var installer = Installer();
        Assert.True(installer.Included);
        Assert.Equal(new PluginStatus(ScsPluginState.NotInstalled, _gameDir), installer.Status(ScsGame.Ets2));

        var result = installer.Install(ScsGame.Ets2);

        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        Assert.Equal(ShippedBytes, File.ReadAllBytes(Target));
        Assert.Equal(ScsPluginState.UpToDate, installer.Status(ScsGame.Ets2).State);
        Assert.Empty(TempFiles(_pluginDir));
    }

    // Why: after an app update the shipped DLL differs; Settings must offer "Update", and updating must really replace the file.
    [Fact]
    public void A_different_file_is_an_available_update_and_gets_replaced()
    {
        Directory.CreateDirectory(_pluginDir);
        File.WriteAllBytes(Target, OldBytes);
        var installer = Installer();
        Assert.Equal(ScsPluginState.UpdateAvailable, installer.Status(ScsGame.Ets2).State);

        Assert.Equal(PluginInstallOutcome.Installed, installer.Install(ScsGame.Ets2).Outcome);

        Assert.Equal(ScsPluginState.UpToDate, installer.Status(ScsGame.Ets2).State);
        Assert.Equal(ShippedBytes, File.ReadAllBytes(Target));
    }

    [Fact]
    public void Installing_twice_is_fine()
    {
        var installer = Installer();
        Assert.Equal(PluginInstallOutcome.Installed, installer.Install(ScsGame.Ets2).Outcome);
        Assert.Equal(PluginInstallOutcome.Installed, installer.Install(ScsGame.Ets2).Outcome);
        Assert.Equal(ShippedBytes, File.ReadAllBytes(Target));
    }

    [Fact]
    public void A_missing_plugin_folder_is_created()
    {
        Assert.False(Directory.Exists(_pluginDir));

        Assert.Equal(PluginInstallOutcome.Installed, Installer().Install(ScsGame.Ets2).Outcome);

        Assert.True(File.Exists(Target));
    }

    [Fact]
    public void A_game_that_is_not_found_is_reported_and_nothing_is_written()
    {
        var installer = Installer();

        Assert.Equal(new PluginStatus(ScsPluginState.GameNotFound, null), installer.Status(ScsGame.Ats));
        Assert.Equal(PluginInstallOutcome.GameNotFound, installer.Install(ScsGame.Ats).Outcome);
    }

    [Fact]
    public void A_build_without_the_dll_says_not_included()
    {
        foreach (var path in new[] { null, _steam.Temp.Combine("nope", "button_box_scs.dll") })
        {
            var installer = new PluginInstaller(_steam.Locator(), path, _ => false);

            Assert.False(installer.Included);
            Assert.Equal(PluginInstallOutcome.NotIncluded, installer.Install(ScsGame.Ets2).Outcome);
        }

        Assert.False(Directory.Exists(_pluginDir));
    }

    // Why: the user must see "close the game first", not a half-done install, and the DLL the game loaded must stay as it was.
    [Fact]
    public void A_target_another_process_holds_open_is_game_running_and_stays_unchanged()
    {
        Directory.CreateDirectory(_pluginDir);
        File.WriteAllBytes(Target, OldBytes);
        using var holder = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = Installer().Install(ScsGame.Ets2);

        Assert.Equal(PluginInstallOutcome.GameRunning, result.Outcome);
        holder.Dispose();
        Assert.Equal(OldBytes, File.ReadAllBytes(Target));
        Assert.Empty(TempFiles(_pluginDir));
    }

    // Why: this is the real failure. A loaded DLL is mapped as an image; replacing it fails with access denied, not a sharing violation.
    [Fact]
    public void A_dll_that_is_loaded_in_a_process_is_game_running_and_stays_unchanged()
    {
        Directory.CreateDirectory(_pluginDir);
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Target);
        var original = File.ReadAllBytes(Target);
        var handle = NativeLibrary.Load(Target);
        try
        {
            var result = Installer().Install(ScsGame.Ets2);

            Assert.Equal(PluginInstallOutcome.GameRunning, result.Outcome);
            Assert.Equal(original, File.ReadAllBytes(Target));
            Assert.Empty(TempFiles(_pluginDir));
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [Fact]
    public void A_running_game_process_refuses_before_anything_is_written()
    {
        // A harmless stand-in: a copy of ping.exe named like the game executable, started from the temp game folder.
        var exe = Path.Combine(_gameDir, "bin", "win_x64", "eurotrucks2.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe, overwrite: true);
        using var process = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "-n", "100", "127.0.0.1" }, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        try
        {
            var result = new PluginInstaller(_steam.Locator(), _shipped).Install(ScsGame.Ets2);

            Assert.Equal(PluginInstallOutcome.GameRunning, result.Outcome);
            Assert.False(Directory.Exists(_pluginDir));
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }
    }

    // Why: Windows reports a running image by its final path; the game folder given to the installer may be spelled through a junction.
    [Fact]
    public void A_running_game_is_found_when_the_game_folder_is_spelled_through_a_junction()
    {
        var exe = Path.Combine(_gameDir, "bin", "win_x64", "eurotrucks2.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe, overwrite: true);
        var link = _steam.Temp.Junction(_steam.Temp.Combine("GameLink"), _gameDir);
        using var process = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "-n", "100", "127.0.0.1" }, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        try
        {
            var installer = new PluginInstaller(new FixedLocator(new ScsGameInstall(ScsGame.Ets2, link)), _shipped);

            Assert.Equal(PluginInstallOutcome.GameRunning, installer.Install(ScsGame.Ets2).Outcome);
            Assert.False(Directory.Exists(_pluginDir));
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }
    }

    [Fact]
    public void A_process_with_the_same_name_from_another_folder_is_not_the_game()
    {
        // Why: matching by name alone would block installs whenever any unrelated eurotrucks2.exe runs.
        var elsewhere = _steam.Temp.MakeDir("Elsewhere");
        var exe = Path.Combine(elsewhere, "eurotrucks2.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        using var process = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "-n", "100", "127.0.0.1" }, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        try
        {
            var result = new PluginInstaller(_steam.Locator(), _shipped).Install(ScsGame.Ets2);

            Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }
    }

    // Why: a plugins folder that is a junction would send the write somewhere else on disk.
    [Fact]
    public void A_plugins_folder_that_is_a_junction_is_refused_and_its_target_untouched()
    {
        var outside = _steam.Temp.MakeDir("Outside");
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        _steam.Temp.Junction(_pluginDir, outside);

        var result = Installer().Install(ScsGame.Ets2);

        Assert.Equal(PluginInstallOutcome.Failed, result.Outcome);
        Assert.Equal(["keep.txt"], Directory.GetFiles(outside).Select(Path.GetFileName));
    }

    [Fact]
    public void A_target_dll_that_is_a_junction_is_refused_and_its_target_untouched()
    {
        var outside = _steam.Temp.MakeDir("OutsideTarget");
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateDirectory(_pluginDir);
        _steam.Temp.Junction(Target, outside);

        var result = Installer().Install(ScsGame.Ets2);

        Assert.Equal(PluginInstallOutcome.Failed, result.Outcome);
        Assert.Equal(["keep.txt"], Directory.GetFiles(outside).Select(Path.GetFileName));
        Assert.Empty(TempFiles(_pluginDir));
    }

    [Fact]
    public void A_target_dll_that_is_a_symbolic_link_is_refused_and_its_target_untouched()
    {
        var outsideFile = _steam.Temp.WriteFile(@"Outside\victim.dll", "victim");
        Directory.CreateDirectory(_pluginDir);
        try
        {
            File.CreateSymbolicLink(Target, outsideFile);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Creating a file symbolic link needs a privilege (developer mode or admin) that this account lacks: " + e.Message);
        }

        var result = Installer().Install(ScsGame.Ets2);

        Assert.Equal(PluginInstallOutcome.Failed, result.Outcome);
        Assert.Equal("victim", File.ReadAllText(outsideFile));
        Assert.Empty(TempFiles(_pluginDir));
    }

    [Fact]
    public void A_folder_named_like_the_dll_is_refused()
    {
        Directory.CreateDirectory(Target);

        Assert.Equal(PluginInstallOutcome.Failed, Installer().Install(ScsGame.Ets2).Outcome);
        Assert.True(Directory.Exists(Target));
    }

    [Fact]
    public void A_file_named_plugins_is_refused()
    {
        File.WriteAllText(_pluginDir, "i am a file");

        Assert.Equal(PluginInstallOutcome.Failed, Installer().Install(ScsGame.Ets2).Outcome);
        Assert.Equal("i am a file", File.ReadAllText(_pluginDir));
    }

    // Why: a Steam library behind a junction is normal; only the plugins folder and the DLL themselves must not be links.
    [Fact]
    public void A_junction_higher_up_in_the_path_is_fine()
    {
        var link = _steam.Temp.Junction(_steam.Temp.Combine("GameLink"), _gameDir);
        var locator = new FixedLocator(new ScsGameInstall(ScsGame.Ets2, link));
        var installer = new PluginInstaller(locator, _shipped, _ => false);

        Assert.Equal(PluginInstallOutcome.Installed, installer.Install(ScsGame.Ets2).Outcome);

        Assert.Equal(ShippedBytes, File.ReadAllBytes(Target));
        Assert.Equal(ScsPluginState.UpToDate, installer.Status(ScsGame.Ets2).State);
    }

    [Fact]
    public void The_bin_folder_is_never_created()
    {
        var empty = _steam.Temp.MakeDir("EmptyGame");
        var installer = new PluginInstaller(new FixedLocator(new ScsGameInstall(ScsGame.Ets2, empty)), _shipped, _ => false);

        Assert.Equal(PluginInstallOutcome.Failed, installer.Install(ScsGame.Ets2).Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(empty));
    }

    // Why: the plugins folder holds other plug-ins of the user (telemetry plug-ins); they must survive install and update.
    [Fact]
    public void Other_files_in_the_plugin_folder_are_never_touched()
    {
        Directory.CreateDirectory(_pluginDir);
        var others = new Dictionary<string, string>
        {
            ["scs-telemetry.dll"] = "another plug-in",
            ["button_box_scs.dll.bak"] = "backup",
            ["button_box_scs.dll.old.tmp"] = "somebody's temp file",
            ["notes.txt"] = "notes",
        };
        foreach (var (name, content) in others)
        {
            File.WriteAllText(Path.Combine(_pluginDir, name), content);
        }

        File.WriteAllBytes(Target, OldBytes);

        Assert.Equal(PluginInstallOutcome.Installed, Installer().Install(ScsGame.Ets2).Outcome);

        Assert.Equal(others.Count + 1, Directory.GetFileSystemEntries(_pluginDir).Length);
        foreach (var (name, content) in others)
        {
            Assert.Equal(content, File.ReadAllText(Path.Combine(_pluginDir, name)));
        }
    }

    [Fact]
    public void Status_is_read_only()
    {
        Installer().Status(ScsGame.Ets2);

        Assert.False(Directory.Exists(_pluginDir));
    }

    [Fact]
    public async Task Two_installs_at_once_run_one_after_the_other()
    {
        var inside = 0;
        var maxInside = 0;
        bool Slow(string _)
        {
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref maxInside, now);
            Thread.Sleep(150);
            Interlocked.Decrement(ref inside);
            return false;
        }

        var installer = Installer(Slow);
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() => installer.Install(ScsGame.Ets2), TestContext.Current.CancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Equal(PluginInstallOutcome.Installed, r.Outcome));
        Assert.Equal(1, maxInside);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
        }
        while (value > current && Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private sealed class FixedLocator(params ScsGameInstall[] installs) : IScsGameLocator
    {
        public IReadOnlyList<ScsGameInstall> Find() => installs;
    }
}
