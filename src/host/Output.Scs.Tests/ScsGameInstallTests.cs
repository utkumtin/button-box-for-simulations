using ButtonBox.Core.Contracts;
using Xunit;

namespace ButtonBox.Output.Scs.Tests;

// Why: the pipe server check trusts exactly this exe path (decisions §9) and the installer writes exactly into this folder
// (decisions §12). A wrong executable name would reject the real game; a wrong folder would install where the game never looks.
public class ScsGameInstallTests
{
    [Theory]
    [InlineData(ScsGame.Ets2, 227300, @"D:\Games\Euro Truck Simulator 2\bin\win_x64\eurotrucks2.exe")]
    [InlineData(ScsGame.Ats, 270880, @"D:\Games\Euro Truck Simulator 2\bin\win_x64\amtrucks.exe")]
    public void Game_has_its_steam_app_id_and_64_bit_exe(ScsGame game, int appId, string exe)
    {
        var install = new ScsGameInstall(game, @"D:\Games\Euro Truck Simulator 2");
        Assert.Equal(appId, ScsGameInstall.AppId(game));
        Assert.Equal(exe, install.ExePath);
        Assert.Equal(@"D:\Games\Euro Truck Simulator 2\bin\win_x64\plugins", install.PluginDir);
    }
}
