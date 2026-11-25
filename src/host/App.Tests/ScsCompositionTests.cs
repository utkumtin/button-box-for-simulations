using System.Net;
using System.Text.Json;
using ButtonBox.App.Lan;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Discovery;
using ButtonBox.Output.Scs;
using ButtonBox.Output.Scs.Install;
using ButtonBox.Output.Scs.Protocol;
using ButtonBox.Security;
using ButtonBox.Simulator;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ButtonBox.App.Tests;

internal sealed class FakeLocator(params ScsGameInstall[] installs) : IScsGameLocator
{
    public IReadOnlyList<ScsGameInstall> Find() => installs;
}

/// <summary>
/// Builds <see cref="AppHost"/>s on a throwaway data folder. It is the only place the App tests make <see cref="AppHostOptions"/>, and it
/// refuses the production pieces: a test host must never reach the real pipe (a running game would pass the server check and could be
/// driven by the test) or the real Steam setup.
/// </summary>
internal sealed class AppHostKit : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-scs-host-" + Guid.NewGuid().ToString("N"));
    private readonly string _keyName = "bb-test-" + Guid.NewGuid().ToString("N");
    private readonly List<NetworkWatcher> _watchers = [];
    private readonly List<AppHost> _hosts = [];

    public string DataDir => Path.Combine(_root, "data");

    /// <summary>A pipe name no game and no other test serves.</summary>
    public static ScsBridgeOptions UniquePipe() => new()
    {
        PipeName = "bbfs-test-" + Guid.NewGuid().ToString("N"),
        ConnectTimeout = TimeSpan.FromMilliseconds(30),
        ReconnectDelay = TimeSpan.FromMilliseconds(30),
    };

    public AppHostOptions Options(
        bool useSimulator = false, IScsGameLocator? locator = null, ScsBridgeOptions? bridge = null, string? pluginDll = null, TimeProvider? time = null)
    {
        if (locator is SteamGameLocator)
        {
            throw new InvalidOperationException("A test must never use the real Steam locator.");
        }

        if (bridge is not null && bridge.PipeName == new ScsBridgeOptions().PipeName)
        {
            throw new InvalidOperationException("A test must never use the real pipe name.");
        }

        var client = Path.Combine(_root, "client");
        var shipped = Path.Combine(_root, "shipped");
        Directory.CreateDirectory(client);
        Directory.CreateDirectory(shipped);
        File.WriteAllText(Path.Combine(shipped, "iphone-14-pro.json"), LanFixture.ShippedTemplateJson);
        File.WriteAllText(Path.Combine(client, "index.html"), "x");
        var watcher = new NetworkWatcher(() => [new LanAddress(1, LanFixture.FakeLan, 24)], new NoSignals(), new FakeTimeProvider(), TimeSpan.FromSeconds(2));
        _watchers.Add(watcher);
        return new AppHostOptions
        {
            DataDir = DataDir,
            ShippedTemplatesDir = shipped,
            ShippedCatalogsDir = Path.Combine(AppContext.BaseDirectory, "catalog"),
            ClientDir = client,
            LogoPng = LanFixture.MakeLogo(),
            Network = watcher,
            ServerKeyName = _keyName,
            HttpsPort = LanFixture.FreePort(),
            PairingPort = LanFixture.FreePort(),
            PairingFallbackPort = LanFixture.FreePort(),
            MdnsPort = LanFixture.FreePort(),
            BindAddress = _ => IPAddress.Loopback,
            Time = time ?? TimeProvider.System,
            UseSimulator = useSimulator,
            ScsLocator = locator,
            ScsBridgeOptions = bridge,
            ScsPluginDll = pluginDll,
        };
    }

    public AppHost Create(AppHostOptions options)
    {
        var host = AppHost.Create(options);
        _hosts.Add(host);
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        CertificateAuthority.DeleteServerKey(_keyName);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder.
        }
    }

    private sealed class NoSignals : INetworkChangeSource
    {
        public event Action? Signaled
        {
            add { }
            remove { }
        }
    }
}

// Why: the app must use the real SCS bridge for ETS2/ATS profiles and the Simulator only on request (decisions §3), and no test or
// accident may ever reach the player's game through the real pipe.
public sealed class AppHostScsTests : IAsyncDisposable
{
    private readonly AppHostKit _kit = new();

    public ValueTask DisposeAsync() => _kit.DisposeAsync();

    private static ScsGameInstall FakeGame() => new(ScsGame.Ets2, Path.Combine(Path.GetTempPath(), "bb-no-such-game"));

    [Fact]
    public void The_defaults_and_the_test_kit_never_hand_the_real_steam_locator_or_the_real_pipe_to_a_host()
    {
        // Why: a test host that connects to the real pipe would pass the server check when a real game runs the plugin and could press
        // inputs in it. Only Program.Main creates the production pieces.
        var plain = new AppHostOptions
        {
            DataDir = "x",
            ShippedTemplatesDir = "x",
            ShippedCatalogsDir = "x",
            ClientDir = "x",
            LogoPng = [],
            Network = null!,
        };
        var kit = _kit.Options();
        var withPieces = _kit.Options(locator: new FakeLocator(), bridge: AppHostKit.UniquePipe());

        foreach (var options in new[] { plain, kit })
        {
            Assert.Null(options.ScsLocator);
            Assert.Null(options.ScsBridgeOptions);
            Assert.Null(options.ScsPluginDll);
            Assert.False(options.UseSimulator);
        }

        Assert.IsNotType<SteamGameLocator>(withPieces.ScsLocator);
        Assert.NotEqual(ScsProtocol.PipeName, withPieces.ScsBridgeOptions!.PipeName);
        Assert.NotEqual(new ScsBridgeOptions().PipeName, withPieces.ScsBridgeOptions.PipeName);
    }

    [Fact]
    public void Without_the_scs_pieces_the_simulator_runs_whatever_the_flag_says()
    {
        // Why: AppHost only gets the bridge from Program.Main; an older or future test that gives nothing must land on the Simulator.
        foreach (var flag in new[] { false, true })
        {
            var host = _kit.Create(_kit.Options(useSimulator: flag));

            Assert.NotNull(host.Simulator);
            Assert.Same(host.Simulator, host.State);
            Assert.Null(host.ScsLink);
            Assert.Null(host.ScsInstaller);
        }
    }

    [Fact]
    public async Task Without_the_flag_the_bridge_is_the_backend_it_is_started_and_there_is_no_simulator()
    {
        var host = _kit.Create(_kit.Options(locator: new FakeLocator(FakeGame()), bridge: AppHostKit.UniquePipe()));

        Assert.Null(host.Simulator);
        Assert.IsType<GameBackend>(host.State);
        Assert.NotNull(host.ScsInstaller);
        Assert.NotNull(host.Session.Engine); // the seeded ETS2 starter is active

        // Started: the connect loop has run and found no game serving the pipe (nothing is started by AppHost.Create's caller).
        await LanRig.WaitForAsync(() => host.ScsLink, link => link is { Connected: false, Failure: ScsBridgeFailure.PipeNotFound });
        Assert.False(host.State.Current.Available);
        Assert.Equal(TestPressResult.Sent, host.Session.TestPress("high-beam")); // goes to the router, which hands it to a bridge that drops it
    }

    [Fact]
    public void With_the_flag_the_simulator_is_the_backend_and_the_installer_still_exists()
    {
        var host = _kit.Create(_kit.Options(useSimulator: true, locator: new FakeLocator(FakeGame()), bridge: AppHostKit.UniquePipe()));

        Assert.NotNull(host.Simulator);
        Assert.Same(host.Simulator, host.State);
        Assert.Null(host.ScsLink); // no bridge: the settings panel shows "not connected"
        Assert.NotNull(host.ScsInstaller); // the panel works in both modes
    }

    [Fact]
    public void The_tray_has_the_simulator_submenu_only_while_the_simulator_runs()
    {
        var simulating = _kit.Create(_kit.Options(useSimulator: true));
        var real = _kit.Create(_kit.Options(locator: new FakeLocator(FakeGame()), bridge: AppHostKit.UniquePipe()));

        var item = TrayApplicationContext.SimulatorItem(simulating);

        Assert.NotNull(item);
        Assert.Equal("Simulator", item.Text);
        Assert.Null(TrayApplicationContext.SimulatorItem(real));
    }

    [Fact]
    public void The_first_run_seed_is_the_full_preset_profile()
    {
        // Why: decisions §5 "Widget behaviours": the first-run seed uses the presets, so a new user starts with the working lights,
        // wipers and engine button. The host was created through the profile gate: a profile that failed it would be in FirstRunProblems.
        var host = _kit.Create(_kit.Options());

        Assert.Empty(host.FirstRunProblems);
        var profile = host.Profiles.Get(host.Profiles.ActiveId!).Value!;
        var widgets = profile.Layouts.Single().Widgets;

        var lights = Assert.IsType<SwitchWidget>(widgets.Single(w => w.Id == "lights"));
        Assert.Equal(SwitchBehavior.Headlights, lights.Behavior);
        Assert.Equal(4, lights.Positions.Count);
        var high = lights.Positions[3];
        Assert.Equal(60, high.Angle);
        Assert.Equal("car-light-high", high.Icon);
        Assert.Equal("light-on", high.Action);

        var wipers = Assert.IsType<SwitchWidget>(widgets.Single(w => w.Id == "wipers"));
        Assert.Equal(SwitchBehavior.Wipers, wipers.Behavior);

        var engine = Assert.IsType<DualActionWidget>(widgets.Single(w => w.Id == "engine"));
        Assert.Equal(DualActionBehavior.EngineStartStop, engine.Behavior);
        Assert.Null(engine.State); // lit and LED come from the behaviour

        Assert.Contains(widgets, w => w.Id == "high-beam"); // the rest of the starter stays
    }

    [Fact]
    public async Task The_engines_the_store_builds_and_the_session_run_on_the_one_clock_of_the_host()
    {
        // Why: a layout engine's deadline is an absolute time on its own clock, and the session arms its timer from it. Engines on
        // the system clock under a session on another clock would never show the amber LED (or spin the timer).
        var time = new TimerSpyTime();
        var host = _kit.Create(_kit.Options(time: time));
        host.Simulator!.EngineNeverStarts = true;
        await using var fx = new LanFixture();
        await using var rig = await LanRig.StartAsync(fx, new DeviceConnectionsOptions { Time = fx.Time, MessagesPerSecond = 10_000 }, session: host.Session, assets: host.Assets);
        var (_, token) = fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        using var socket = await rig.ConnectAsync();
        await LanRig.SendTextAsync(socket, LanRig.Hello(token));
        _ = await ReadStateAsync(socket, _ => true);
        await host.Simulator.PressAsync(new ScsInput("ignitionon"), LanFixture.Ct); // electric on, as from the keyboard
        _ = await ReadStateAsync(socket, f => IsLit(f, "engine"));

        await LanRig.SendTextAsync(socket, """{"type":"gesture","widgetId":"engine","gesture":"hold"}""");
        await LanRig.WaitForAsync(() => host.Simulator.Received.Count, n => n >= 2); // the starter reached the game
        // The session armed a timer of the host clock for the amber deadline (a session on another clock would arm its own).
        Assert.Contains(TimeSpan.FromSeconds(4), time.Dues);
        time.Advance(TimeSpan.FromSeconds(4));

        var frame = await ReadStateAsync(socket, _ => true); // the very next state frame is the amber one
        Assert.Equal("amber", LedOf(frame, "engine"));
    }

    // A fake clock that remembers the due times set on its timers.
    private sealed class TimerSpyTime : FakeTimeProvider
    {
        private readonly List<TimeSpan> _dues = [];

        public IReadOnlyList<TimeSpan> Dues
        {
            get
            {
                lock (_dues)
                {
                    return [.. _dues];
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new SpyTimer(base.CreateTimer(callback, state, dueTime, period), _dues);

        private sealed class SpyTimer(ITimer inner, List<TimeSpan> dues) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (dues)
                {
                    dues.Add(dueTime);
                }

                return inner.Change(dueTime, period);
            }

            public void Dispose() => inner.Dispose();

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static async Task<string> ReadStateAsync(System.Net.WebSockets.WebSocket socket, Func<string, bool> match)
    {
        while (true)
        {
            var frame = await LanRig.ReadFrameAsync(socket);
            using var doc = JsonDocument.Parse(frame);
            if (doc.RootElement.GetProperty("type").GetString() == "state" && match(frame))
            {
                return frame;
            }
        }
    }

    private static bool IsLit(string stateFrame, string widget)
    {
        using var doc = JsonDocument.Parse(stateFrame);
        return doc.RootElement.GetProperty("widgets").GetProperty(widget).TryGetProperty("lit", out var lit) && lit.GetBoolean();
    }

    private static string? LedOf(string stateFrame, string widget)
    {
        using var doc = JsonDocument.Parse(stateFrame);
        return doc.RootElement.GetProperty("widgets").GetProperty(widget).TryGetProperty("led", out var led) ? led.GetString() : null;
    }
}

// Why: ETS2 and ATS profiles must drive the game; every other game has no backend yet and must neither show stale SCS telemetry nor
// send presses into a game it does not belong to (decisions §3 "Backend selection").
public sealed class GameBackendTests
{
    private static readonly ScsInput Press = new("lighton");

    private sealed class FakeScs : IOutputBackend, IStateProvider
    {
        private TelemetrySnapshot _current = new(true, true, new Dictionary<string, int> { ["truck.light.beam.high"] = 1 });

        public List<GameOutput> Pressed { get; } = [];

        public TelemetrySnapshot Current => _current;

        public event EventHandler<TelemetrySnapshot>? Changed;

        public Task PressAsync(GameOutput output, CancellationToken cancellationToken = default)
        {
            Pressed.Add(output);
            return Task.CompletedTask;
        }

        public Task HoldAsync(GameOutput output, CancellationToken cancellationToken = default)
        {
            Pressed.Add(output);
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(GameOutput output, CancellationToken cancellationToken = default)
        {
            Pressed.Add(output);
            return Task.CompletedTask;
        }

        public void Raise()
        {
            _current = new TelemetrySnapshot(true, true, new Dictionary<string, int> { ["truck.light.beam.high"] = 0 });
            Changed?.Invoke(this, _current);
        }
    }

    [Theory]
    [InlineData(Game.Ets2)]
    [InlineData(Game.Ats)]
    public async Task An_scs_profile_gets_the_bridges_state_and_presses(Game game)
    {
        var scs = new FakeScs();
        var router = new GameBackend(() => game, scs, scs);

        await router.PressAsync(Press, TestContext.Current.CancellationToken);
        await router.HoldAsync(Press, TestContext.Current.CancellationToken);
        await router.ReleaseAsync(Press, TestContext.Current.CancellationToken);

        Assert.Same(scs.Current, router.Current);
        Assert.Equal([Press, Press, Press], scs.Pressed);
    }

    [Theory]
    [InlineData(Game.Acc)]
    [InlineData(Game.Ac1)]
    [InlineData(Game.Acevo)]
    public async Task Another_game_gets_no_telemetry_and_its_presses_never_reach_the_bridge(Game game)
    {
        var scs = new FakeScs();
        var router = new GameBackend(() => game, scs, scs);

        await router.PressAsync(Press, TestContext.Current.CancellationToken);
        await router.HoldAsync(Press, TestContext.Current.CancellationToken);
        await router.ReleaseAsync(Press, TestContext.Current.CancellationToken);

        Assert.Same(TelemetrySnapshot.None, router.Current);
        Assert.Empty(scs.Pressed);
    }

    [Fact]
    public async Task Without_an_active_profile_there_is_no_backend()
    {
        var scs = new FakeScs();
        var router = new GameBackend(() => null, scs, scs);

        await router.PressAsync(Press, TestContext.Current.CancellationToken);

        Assert.Same(TelemetrySnapshot.None, router.Current);
        Assert.Empty(scs.Pressed);
    }

    [Fact]
    public async Task Switching_the_active_profile_switches_the_source()
    {
        var scs = new FakeScs();
        Game? active = Game.Ets2;
        var router = new GameBackend(() => active, scs, scs);
        await router.PressAsync(Press, TestContext.Current.CancellationToken);
        Assert.True(router.Current.Available);

        active = Game.Acc;
        await router.PressAsync(Press, TestContext.Current.CancellationToken);
        Assert.False(router.Current.Available);

        active = Game.Ats;
        await router.PressAsync(Press, TestContext.Current.CancellationToken);
        Assert.True(router.Current.Available);
        Assert.Equal(2, scs.Pressed.Count); // the press while ACC was active was dropped
    }

    [Fact]
    public void A_change_of_the_bridge_is_forwarded_only_while_an_scs_profile_is_active()
    {
        // Why: a profile switch already refreshes every phone; an SCS change while ACC runs must not wake them with a "no telemetry" state.
        var scs = new FakeScs();
        Game? active = Game.Ets2;
        var router = new GameBackend(() => active, scs, scs);
        var seen = new List<TelemetrySnapshot>();
        router.Changed += (_, s) => seen.Add(s);

        scs.Raise();
        Assert.Single(seen);
        Assert.Same(scs.Current, seen[0]);

        active = Game.Acc;
        scs.Raise();
        Assert.Single(seen);

        active = null;
        scs.Raise();
        Assert.Single(seen);
    }
}
