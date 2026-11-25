using ButtonBox.Core.Contracts;
using ButtonBox.Output.Scs.Install;

namespace ButtonBox.App;

/// <summary>
/// What the editor API needs from the plugin installer: the state per game and the install. The seam lets the route tests drive every
/// outcome (including "the game runs") without a game process. Both members do file I/O: call them off the UI thread.
/// </summary>
public interface IScsPluginInstaller
{
    /// <summary>The shipped DLL exists in this build.</summary>
    bool Included { get; }

    PluginStatus Status(ScsGame game);

    PluginInstallResult Install(ScsGame game);
}

/// <summary>The production <see cref="IScsPluginInstaller"/>: the <see cref="PluginInstaller"/> of the Output.Scs project.</summary>
public sealed class ScsPluginInstaller(PluginInstaller inner) : IScsPluginInstaller
{
    public bool Included => inner.Included;

    public PluginStatus Status(ScsGame game) => inner.Status(game);

    public PluginInstallResult Install(ScsGame game) => inner.Install(game);
}
