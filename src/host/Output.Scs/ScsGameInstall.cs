using ButtonBox.Core.Contracts;

namespace ButtonBox.Output.Scs;

/// <summary>
/// One installed SCS game found through Steam (decisions §12 "SCS plugin install").
/// </summary>
/// <param name="Game">ETS2 or ATS.</param>
/// <param name="InstallDir">The game folder, full and normalized (e.g. <c>D:\SteamLibrary\steamapps\common\American Truck Simulator</c>).</param>
public sealed record ScsGameInstall(ScsGame Game, string InstallDir)
{
    /// <summary>The 64-bit game executable: the only image path the pipe server check accepts (decisions §9).</summary>
    public string ExePath => Path.Combine(InstallDir, "bin", "win_x64", ExeName(Game));

    /// <summary>The folder the game loads plugins from; it may not exist yet.</summary>
    public string PluginDir => Path.Combine(InstallDir, "bin", "win_x64", "plugins");

    /// <summary>Steam app id: ETS2 227300, ATS 270880 (verified on the user's PC, 2026-11-25).</summary>
    public static int AppId(ScsGame game) => game switch
    {
        ScsGame.Ets2 => 227300,
        ScsGame.Ats => 270880,
        _ => throw new ArgumentOutOfRangeException(nameof(game)),
    };

    public static string ExeName(ScsGame game) => game switch
    {
        ScsGame.Ets2 => "eurotrucks2.exe",
        ScsGame.Ats => "amtrucks.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(game)),
    };
}

/// <summary>
/// Finds the installed SCS games. Read on demand (Settings panel, install, every pipe connect), so a game installed or moved while
/// the app runs is picked up without a restart.
/// </summary>
public interface IScsGameLocator
{
    /// <summary>The games found now; empty when Steam or the games are missing. Never throws for broken Steam files.</summary>
    IReadOnlyList<ScsGameInstall> Find();
}
