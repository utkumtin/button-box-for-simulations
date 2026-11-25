using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ButtonBox.Core.Contracts;
using ButtonBox.Output.Scs;
using ButtonBox.Output.Scs.Install;
using Xunit;

namespace ButtonBox.App.Tests;

internal sealed class FakeInstaller : IScsPluginInstaller
{
    private readonly Dictionary<ScsGame, PluginStatus> _status = new()
    {
        [ScsGame.Ets2] = new PluginStatus(ScsPluginState.GameNotFound, null),
        [ScsGame.Ats] = new PluginStatus(ScsPluginState.GameNotFound, null),
    };

    private int _installs;

    public bool Included { get; set; } = true;

    public PluginInstallResult Result { get; set; } = new(PluginInstallOutcome.Installed);

    public List<ScsGame> Installed { get; } = [];

    public int InstallCalls => Volatile.Read(ref _installs);

    public FakeInstaller With(ScsGame game, ScsPluginState state, string? dir = null)
    {
        _status[game] = new PluginStatus(state, dir);
        return this;
    }

    public PluginStatus Status(ScsGame game) => _status[game];

    public PluginInstallResult Install(ScsGame game)
    {
        Interlocked.Increment(ref _installs);
        Installed.Add(game);
        return Result;
    }
}

// Why: the Settings panel tells the user whether the plugin can be installed and whether the game is connected, and its button
// copies a DLL into the game folder (decisions §11, §12). The answers must map one to one, and nothing may install without the
// cookie, the Origin and a small JSON body.
public sealed class ScsEditorApiTests
{
    private const string Api = "/editor/api";
    private const string InstallPath = Api + "/settings/scs-plugin/install";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> BodyAsync(HttpResponseMessage r) => await r.Content.ReadAsStringAsync(Ct);

    // ---- settings ----

    [Fact]
    public async Task Settings_without_an_installer_say_not_included_and_both_games_not_found()
    {
        await using var rig = await Api4Rig.StartAsync();

        var plugin = (await rig.GetJsonAsync<SettingsStatus>(Api + "/settings")).ScsPlugin;

        Assert.False(plugin.Included);
        Assert.Equal([ScsGame.Ets2, ScsGame.Ats], plugin.Games.Select(g => g.Game));
        Assert.All(plugin.Games, g =>
        {
            Assert.Equal(ScsPluginState.GameNotFound, g.State);
            Assert.False(g.Connected);
        });
    }

    [Theory]
    [InlineData(ScsPluginState.GameNotFound, ScsPluginState.UpToDate)]
    [InlineData(ScsPluginState.NotInstalled, ScsPluginState.UpdateAvailable)]
    [InlineData(ScsPluginState.UpToDate, ScsPluginState.NotInstalled)]
    [InlineData(ScsPluginState.UpdateAvailable, ScsPluginState.GameNotFound)]
    public async Task Settings_map_each_installer_state_per_game_ets2_first(ScsPluginState ets2, ScsPluginState ats)
    {
        // Two different states: a mix-up of the games would show.
        var installer = new FakeInstaller { Included = true }
            .With(ScsGame.Ets2, ets2, ets2 == ScsPluginState.GameNotFound ? null : @"C:\Games\ETS2")
            .With(ScsGame.Ats, ats, ats == ScsPluginState.GameNotFound ? null : @"D:\Games\ATS");
        await using var rig = await Api4Rig.StartAsync(scsInstaller: installer);

        var plugin = (await rig.GetJsonAsync<SettingsStatus>(Api + "/settings")).ScsPlugin;

        Assert.True(plugin.Included);
        Assert.Equal([ScsGame.Ets2, ScsGame.Ats], plugin.Games.Select(g => g.Game));
        Assert.Equal([ets2, ats], plugin.Games.Select(g => g.State));
        Assert.Equal([ets2 == ScsPluginState.GameNotFound ? null : @"C:\Games\ETS2", ats == ScsPluginState.GameNotFound ? null : @"D:\Games\ATS"], plugin.Games.Select(g => g.InstallDir));
    }

    [Fact]
    public async Task Settings_say_not_included_when_the_build_ships_no_dll()
    {
        await using var rig = await Api4Rig.StartAsync(scsInstaller: new FakeInstaller { Included = false }.With(ScsGame.Ets2, ScsPluginState.NotInstalled, "x"));

        var plugin = (await rig.GetJsonAsync<SettingsStatus>(Api + "/settings")).ScsPlugin;

        Assert.False(plugin.Included);
        Assert.Equal(ScsPluginState.NotInstalled, plugin.Games[0].State);
    }

    [Fact]
    public async Task Connected_is_true_only_for_the_game_the_hello_named()
    {
        var installer = new FakeInstaller()
            .With(ScsGame.Ets2, ScsPluginState.UpToDate, "e")
            .With(ScsGame.Ats, ScsPluginState.UpToDate, "a");
        await using var rig = await Api4Rig.StartAsync(scsInstaller: installer);

        rig.Link = null; // simulator mode: no bridge
        Assert.Equal([false, false], (await rig.GetJsonAsync<SettingsStatus>(Api + "/settings")).ScsPlugin.Games.Select(g => g.Connected));

        rig.Link = new ScsBridgeStatus(true, ScsGame.Ats, ScsBridgeFailure.None);
        Assert.Equal([false, true], (await rig.GetJsonAsync<SettingsStatus>(Api + "/settings")).ScsPlugin.Games.Select(g => g.Connected));

        rig.Link = new ScsBridgeStatus(true, ScsGame.Ets2, ScsBridgeFailure.None);
        Assert.Equal([true, false], (await rig.GetJsonAsync<SettingsStatus>(Api + "/settings")).ScsPlugin.Games.Select(g => g.Connected));

        // Not connected: the last game is remembered by nobody.
        rig.Link = new ScsBridgeStatus(false, ScsGame.Ets2, ScsBridgeFailure.PipeNotFound);
        Assert.Equal([false, false], (await rig.GetJsonAsync<SettingsStatus>(Api + "/settings")).ScsPlugin.Games.Select(g => g.Connected));
    }

    [Fact]
    public async Task A_failed_server_check_never_turns_an_installed_game_into_game_not_found()
    {
        // Why: "game not found" comes from the locator. A pipe served by something else is a different problem and must not make the
        // user believe the game is missing.
        var installer = new FakeInstaller().With(ScsGame.Ets2, ScsPluginState.UpToDate, "e").With(ScsGame.Ats, ScsPluginState.NotInstalled, "a");
        await using var rig = await Api4Rig.StartAsync(scsInstaller: installer);
        rig.Link = new ScsBridgeStatus(false, null, ScsBridgeFailure.ServerCheckFailed);

        var plugin = (await rig.GetJsonAsync<SettingsStatus>(Api + "/settings")).ScsPlugin;

        Assert.Equal([ScsPluginState.UpToDate, ScsPluginState.NotInstalled], plugin.Games.Select(g => g.State));
        Assert.Equal([false, false], plugin.Games.Select(g => g.Connected));
    }

    [Fact]
    public async Task The_real_installer_in_a_temp_folder_maps_to_every_state_and_installs_only_its_dll()
    {
        // Why: the whole chain locator, installer and route against real files, in a temp "game" folder. Nothing outside it is touched.
        var root = Path.Combine(Path.GetTempPath(), "bb-scs-api-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ets2 = new ScsGameInstall(ScsGame.Ets2, Path.Combine(root, "ets2"));
            Directory.CreateDirectory(Path.GetDirectoryName(ets2.ExePath)!);
            var shipped = Path.Combine(root, "shipped", PluginInstaller.DllName);
            Directory.CreateDirectory(Path.GetDirectoryName(shipped)!);
            await File.WriteAllBytesAsync(shipped, [1, 2, 3, 4], Ct);
            var installer = new ScsPluginInstaller(new PluginInstaller(new FakeLocator(ets2), shipped));
            await using var rig = await Api4Rig.StartAsync(scsInstaller: installer);

            var before = (await rig.GetJsonAsync<SettingsStatus>(Api + "/settings")).ScsPlugin;
            Assert.True(before.Included);
            Assert.Equal([ScsPluginState.NotInstalled, ScsPluginState.GameNotFound], before.Games.Select(g => g.State));
            Assert.Equal(ets2.InstallDir, before.Games[0].InstallDir);

            using var ats = await rig.PostAsync(InstallPath, """{"game":"ats"}""");
            Assert.Equal(HttpStatusCode.NotFound, ats.StatusCode);
            Assert.False(Directory.Exists(ets2.PluginDir));

            using var done = await rig.PostAsync(InstallPath, """{"game":"ets2"}""");
            Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
            var target = Path.Combine(ets2.PluginDir, PluginInstaller.DllName);
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(target, Ct));
            Assert.Equal([target], Directory.GetFileSystemEntries(ets2.PluginDir));
            Assert.Equal(ScsPluginState.UpToDate, (await rig.GetJsonAsync<SettingsStatus>(Api + "/settings")).ScsPlugin.Games[0].State);

            // A different DLL in the game (an older plugin, or one with another protocol) is an update, not an error.
            await File.WriteAllBytesAsync(target, [9], Ct);
            Assert.Equal(ScsPluginState.UpdateAvailable, (await rig.GetJsonAsync<SettingsStatus>(Api + "/settings")).ScsPlugin.Games[0].State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- install ----

    [Theory]
    [InlineData(PluginInstallOutcome.Installed, null, HttpStatusCode.NoContent)]
    [InlineData(PluginInstallOutcome.GameNotFound, null, HttpStatusCode.NotFound)]
    [InlineData(PluginInstallOutcome.NotIncluded, null, HttpStatusCode.ServiceUnavailable)]
    public async Task The_install_answers_without_a_body_for_installed_not_found_and_not_included(PluginInstallOutcome outcome, string? reason, HttpStatusCode expected)
    {
        var installer = new FakeInstaller { Result = new PluginInstallResult(outcome, reason) };
        await using var rig = await Api4Rig.StartAsync(scsInstaller: installer);

        using var response = await rig.PostAsync(InstallPath, """{"game":"ats"}""");

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(string.Empty, await BodyAsync(response));
        Assert.Equal([ScsGame.Ats], installer.Installed); // the game of the request, not another
    }

    [Theory]
    [InlineData("ets2", "Close Euro Truck Simulator 2 first.")]
    [InlineData("ats", "Close American Truck Simulator first.")]
    public async Task A_running_game_is_409_with_the_games_own_text(string game, string text)
    {
        var installer = new FakeInstaller { Result = new PluginInstallResult(PluginInstallOutcome.GameRunning) };
        await using var rig = await Api4Rig.StartAsync(scsInstaller: installer);

        using var response = await rig.PostAsync(InstallPath, $$"""{"game":"{{game}}"}""");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problems = JsonSerializer.Deserialize<ProblemList>(await BodyAsync(response), ContractJson.Options)!;
        Assert.Equal([text], problems.Problems);
    }

    [Fact]
    public async Task A_failed_write_is_422_with_the_short_reason()
    {
        var installer = new FakeInstaller { Result = new PluginInstallResult(PluginInstallOutcome.Failed, "the copy did not match the original") };
        await using var rig = await Api4Rig.StartAsync(scsInstaller: installer);

        using var response = await rig.PostAsync(InstallPath, """{"game":"ets2"}""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problems = JsonSerializer.Deserialize<ProblemList>(await BodyAsync(response), ContractJson.Options)!;
        Assert.Equal(["the copy did not match the original"], problems.Problems);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"game":"acc"}""")]
    [InlineData("""{"game":null}""")]
    [InlineData("""{"game":"ets2","extra":1}""")]
    [InlineData("""{"game":1}""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task A_body_that_is_not_an_install_request_is_400_and_installs_nothing(string body)
    {
        var installer = new FakeInstaller();
        await using var rig = await Api4Rig.StartAsync(scsInstaller: installer);

        using var response = await rig.PostAsync(InstallPath, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, installer.InstallCalls);
    }

    [Fact]
    public async Task A_refused_request_installs_nothing()
    {
        // Why: copying into the game folder is the one thing here that leaves the app's own folder; a stray page or process must
        // not be able to start it.
        var installer = new FakeInstaller();
        await using var rig = await Api4Rig.StartAsync(scsInstaller: installer);
        const string Ok = """{"game":"ets2"}""";

        var noCookie = rig.Build("POST", InstallPath, Ok);
        noCookie.Headers.Remove("Cookie");
        var noOrigin = rig.Build("POST", InstallPath, Ok);
        noOrigin.Headers.Remove("Origin");
        var otherOrigin = rig.Build("POST", InstallPath, Ok);
        otherOrigin.Headers.Remove("Origin");
        otherOrigin.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        var text = rig.Build("POST", InstallPath, Ok);
        text.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        var form = rig.Build("POST", InstallPath, Ok);
        form.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=x");
        var get = rig.Build("GET", InstallPath);
        var declared = rig.Build("POST", InstallPath, Ok + new string(' ', EditorApi.MaxBodyBytes));
        var chunked = rig.Build("POST", InstallPath);
        chunked.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(Ok + new string(' ', EditorApi.MaxBodyBytes))));
        chunked.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        chunked.Headers.TransferEncodingChunked = true;

        var answers = new List<HttpStatusCode>();
        foreach (var request in new[] { noCookie, noOrigin, otherOrigin, text, form, get, declared, chunked })
        {
            using var response = await rig.SendAsync(request);
            answers.Add(response.StatusCode);
        }

        Assert.Equal(
            [
                HttpStatusCode.Unauthorized,
                HttpStatusCode.Forbidden,
                HttpStatusCode.Forbidden,
                HttpStatusCode.UnsupportedMediaType,
                HttpStatusCode.UnsupportedMediaType,
                HttpStatusCode.MethodNotAllowed,
                HttpStatusCode.RequestEntityTooLarge,
                HttpStatusCode.RequestEntityTooLarge,
            ],
            answers);
        Assert.Equal(0, installer.InstallCalls);
    }

    [Fact]
    public async Task The_install_route_does_not_exist_for_another_path_shape()
    {
        var installer = new FakeInstaller();
        await using var rig = await Api4Rig.StartAsync(scsInstaller: installer);

        foreach (var path in new[] { "/settings/scs-plugin", "/settings/other/install", "/settings/scs-plugin/install/x", "/settings/scs-plugin/remove" })
        {
            using var response = await rig.PostAsync(Api + path, """{"game":"ets2"}""");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(0, installer.InstallCalls);
    }
}
