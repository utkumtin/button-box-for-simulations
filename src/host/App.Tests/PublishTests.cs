using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ButtonBox.App.Lan;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using ButtonBox.Simulator;
using Xunit;

namespace ButtonBox.App.Tests;

/// <summary>
/// Publishing (decisions §3): the active profile changes while phones are connected and every phone follows in place.
/// Real LAN sockets where a phone is the point; a recording socket where the test needs to step between Open and Start.
/// </summary>
public sealed class PublishTests : IAsyncDisposable
{
    private const string OtherTemplate = "ipad-generic";

    private readonly LanFixture _fx = new();
    private readonly DemoContent _demo = DemoContent.Load();
    private readonly ScsSimulator _sim = new();
    private readonly List<WebSocket> _sockets = [];
    private LanRig? _rig;
    private GameSession? _session;

    private static CancellationToken Ct => LanFixture.Ct;

    public async ValueTask DisposeAsync()
    {
        foreach (var socket in _sockets)
        {
            socket.Dispose();
        }

        if (_rig is not null)
        {
            await _rig.DisposeAsync();
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        await _fx.DisposeAsync();
    }

    private async Task StartAsync(ProfileEngine? engine, IOutputBackend? backend = null)
    {
        _session = new GameSession(engine, backend ?? _sim, _sim);
        _rig = await LanRig.StartAsync(_fx, new DeviceConnectionsOptions { Time = _fx.Time, MessagesPerSecond = 100_000 }, session: _session, assets: _demo.Assets);
    }

    private async Task<WebSocket> ConnectPhoneAsync(string template = LanFixture.TemplateId)
    {
        var (_, token) = _fx.Registry.Add("Phone " + _sockets.Count, template, false);
        var socket = await _rig!.ConnectAsync();
        _sockets.Add(socket);
        await LanRig.SendTextAsync(socket, LanRig.Hello(token));
        return socket;
    }

    // A new engine from the demo profile, the way a Save builds one. A JSON round trip keeps engines from sharing widget objects.
    private ProfileEngine Fresh(Action<Layout>? tweak = null, Action<Profile>? tweakProfile = null)
    {
        var json = JsonSerializer.Serialize(_demo.Profile, ContractJson.Options);
        var profile = JsonSerializer.Deserialize<Profile>(json, ContractJson.Options)!;
        if (tweak is not null)
        {
            foreach (var layout in profile.Layouts)
            {
                tweak(layout);
            }
        }

        tweakProfile?.Invoke(profile);
        return new ProfileEngine(profile, _demo.Catalog);
    }

    private static void Remove(Layout layout, string widgetId) =>
        layout.Widgets = layout.Widgets.Where(w => w.Id != widgetId).ToList();

    private static void Relabel(Layout layout, string widgetId, string label) =>
        layout.Widgets.Single(w => w.Id == widgetId).Label = label;

    // The demo layout again for a second device template.
    private static void AddLayoutFor(Profile profile, string template)
    {
        var json = JsonSerializer.Serialize(profile.Layouts[0], ContractJson.Options);
        var copy = JsonSerializer.Deserialize<Layout>(json, ContractJson.Options)!;
        copy.TemplateId = template;
        profile.Layouts = [.. profile.Layouts, copy];
    }

    private static string LayoutFrame(ProfileEngine engine, string template = LanFixture.TemplateId) =>
        JsonSerializer.Serialize<HostMessage>(new LayoutMessage { Layout = engine.ForTemplate(template)!.Layout }, ContractJson.Options);

    private static string TypeOf(string frame)
    {
        using var doc = JsonDocument.Parse(frame);
        return doc.RootElement.GetProperty("type").GetString()!;
    }

    private static int PositionOf(string stateFrame, string widget)
    {
        using var doc = JsonDocument.Parse(stateFrame);
        return doc.RootElement.GetProperty("widgets").GetProperty(widget).GetProperty("position").GetInt32();
    }

    private static HashSet<string> StateIds(string stateFrame)
    {
        using var doc = JsonDocument.Parse(stateFrame);
        return doc.RootElement.GetProperty("widgets").EnumerateObject().Select(p => p.Name).ToHashSet();
    }

    private static HashSet<string> LayoutIds(string layoutFrame)
    {
        using var doc = JsonDocument.Parse(layoutFrame);
        return doc.RootElement.GetProperty("layout").GetProperty("widgets").EnumerateArray().Select(w => w.GetProperty("id").GetString()!).ToHashSet();
    }

    private static async Task<List<string>> ReadAsync(WebSocket socket, int count)
    {
        var frames = new List<string>();
        for (var i = 0; i < count; i++)
        {
            frames.Add(await LanRig.ReadFrameAsync(socket));
        }

        return frames;
    }

    private static string Gesture(string widget, string gesture) => $$"""{"type":"gesture","widgetId":"{{widget}}","gesture":"{{gesture}}"}""";

    private static string Select(string widget, int position) => $$"""{"type":"select","widgetId":"{{widget}}","position":{{position}}}""";

    [Fact]
    public async Task A_connected_phone_gets_the_new_layout_then_status_then_state_without_reconnecting()
    {
        // §3 "Publishing": the phone must have the new layout before it sees state that refers to it.
        var a = _demo.Engine;
        await StartAsync(a);
        var socket = await ConnectPhoneAsync();
        Assert.Equal(["welcome", "layout", "status", "state"], (await ReadAsync(socket, 4)).Select(TypeOf));
        var deviceId = Assert.Single(_rig!.Connections.ConnectedDeviceIds);

        var b = Fresh(l => Relabel(l, "high-beam", "HIGH BEAM B"));
        _session!.Publish(b, keepSwitchPositions: true);

        var frames = await ReadAsync(socket, 3);
        Assert.Equal(["layout", "status", "state"], frames.Select(TypeOf));
        Assert.Equal(LayoutFrame(b), frames[0]);
        Assert.NotEqual(LayoutFrame(a), frames[0]);
        using (var status = JsonDocument.Parse(frames[1]))
        {
            Assert.True(status.RootElement.GetProperty("telemetry").GetBoolean());
            Assert.True(status.RootElement.GetProperty("gameListening").GetBoolean());
        }

        Assert.Equal(0, PositionOf(frames[2], "lights"));
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.Equal(deviceId, Assert.Single(_rig.Connections.ConnectedDeviceIds)); // the same connection, no reconnect
    }

    [Fact]
    public async Task A_save_with_the_same_template_never_serves_the_cached_layout_of_the_old_engine()
    {
        // The layout frame is cached per template. A cache that outlives the engine would keep showing the phone the old label
        // after a Save, both to phones already connected and to phones that connect afterwards.
        await StartAsync(_demo.Engine);
        var socket = await ConnectPhoneAsync();
        _ = await ReadAsync(socket, 4);

        var b = Fresh(l => Relabel(l, "high-beam", "CHANGED"));
        _session!.Publish(b, keepSwitchPositions: true);

        var frames = await ReadAsync(socket, 3);
        Assert.Contains("CHANGED", frames[0], StringComparison.Ordinal);
        Assert.Equal(LayoutFrame(b), frames[0]);

        var late = await ConnectPhoneAsync();
        var lateFrames = await ReadAsync(late, 2);
        Assert.Equal(LayoutFrame(b), lateFrames[1]);
    }

    [Fact]
    public async Task Publishing_no_engine_gives_every_phone_noLayout_and_status_and_no_state_until_a_profile_is_published_again()
    {
        // §3: no active profile means noLayout. State frames would reference widgets the phone no longer has.
        await StartAsync(_demo.Engine);
        var socket = await ConnectPhoneAsync();
        _ = await ReadAsync(socket, 4);

        _session!.Publish(null, keepSwitchPositions: false);

        Assert.Equal(["noLayout", "status"], (await ReadAsync(socket, 2)).Select(TypeOf));

        // Each telemetry change must produce exactly a status frame: a state frame in between would be read as the next one.
        _sim.GameListening = false;
        var changed = await LanRig.ReadFrameAsync(socket);
        Assert.Equal("status", TypeOf(changed));
        Assert.Contains("\"gameListening\":false", changed, StringComparison.Ordinal);
        _sim.TelemetryAvailable = false;
        Assert.Equal("status", TypeOf(await LanRig.ReadFrameAsync(socket)));

        _sim.TelemetryAvailable = true;
        _sim.GameListening = true;
        var again = Fresh();
        _session.Publish(again, keepSwitchPositions: false);

        var rest = new List<string>();
        while (rest.Count == 0 || TypeOf(rest[^1]) != "state")
        {
            rest.Add(await LanRig.ReadFrameAsync(socket));
        }

        // Whatever status frames the telemetry changes above produced come first; the layout then state resume, in order.
        var layoutAt = rest.FindIndex(f => TypeOf(f) == "layout");
        Assert.True(layoutAt >= 0, "layout must come back");
        Assert.All(rest.Take(layoutAt), f => Assert.Equal("status", TypeOf(f)));
        Assert.Equal(LayoutFrame(again), rest[layoutAt]);
        Assert.Equal("status", TypeOf(rest[layoutAt + 1]));
        Assert.Equal(layoutAt + 3, rest.Count);
    }

    [Fact]
    public async Task A_session_without_an_engine_sends_noLayout_to_a_connecting_phone()
    {
        await StartAsync(null);
        var socket = await ConnectPhoneAsync();

        Assert.Equal(["welcome", "noLayout", "status"], (await ReadAsync(socket, 3)).Select(TypeOf));
        Assert.Null(_session!.Engine);
    }

    [Fact]
    public async Task Each_phone_gets_the_layout_of_its_own_template_and_a_template_without_a_layout_gets_noLayout()
    {
        await StartAsync(_demo.Engine); // the demo profile has a layout for the iPhone only
        var phone = await ConnectPhoneAsync();
        var tablet = await ConnectPhoneAsync(OtherTemplate);
        Assert.Equal(["welcome", "layout", "status", "state"], (await ReadAsync(phone, 4)).Select(TypeOf));
        Assert.Equal(["welcome", "noLayout", "status"], (await ReadAsync(tablet, 3)).Select(TypeOf));

        var both = Fresh(tweakProfile: p => AddLayoutFor(p, OtherTemplate));
        _session!.Publish(both, keepSwitchPositions: false);

        var phoneFrames = await ReadAsync(phone, 3);
        var tabletFrames = await ReadAsync(tablet, 3);
        Assert.Equal(LayoutFrame(both), phoneFrames[0]);
        Assert.Equal(LayoutFrame(both, OtherTemplate), tabletFrames[0]);
        Assert.Contains($"\"templateId\":\"{OtherTemplate}\"", tabletFrames[0], StringComparison.Ordinal);
        Assert.Equal(["layout", "status", "state"], tabletFrames.Select(TypeOf));

        // Back to a profile that only has the iPhone layout: the tablet loses its layout again.
        _session.Publish(Fresh(), keepSwitchPositions: false);

        Assert.Equal(["layout", "status", "state"], (await ReadAsync(phone, 3)).Select(TypeOf));
        Assert.Equal(["noLayout", "status"], (await ReadAsync(tablet, 2)).Select(TypeOf));
    }

    [Fact]
    public async Task A_gesture_for_a_widget_that_only_the_old_layout_had_is_dropped_and_the_connection_stays_open()
    {
        // §3: a gesture for the old layout is stale, not an error. The game must not receive it.
        await StartAsync(_demo.Engine);
        var socket = await ConnectPhoneAsync();
        _ = await ReadAsync(socket, 4);
        _session!.Publish(Fresh(l => Remove(l, "high-beam")), keepSwitchPositions: true);
        _ = await ReadAsync(socket, 3);

        await LanRig.SendTextAsync(socket, Gesture("high-beam", "tap")); // only in the old layout
        await LanRig.SendTextAsync(socket, Gesture("beacon", "tap")); // barrier: handled in order after the stale one

        _ = await LanRig.WaitForAsync(() => _sim.Received.Count, count => count >= 1);
        await Task.Delay(100, Ct);
        Assert.Equal(new ReceivedOutput(OutputKind.Press, new ScsInput("beacon")), Assert.Single(_sim.Received));
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task A_save_keeps_a_commanded_switch_position_and_a_profile_switch_resets_it()
    {
        // §3 "Commanded switch positions": Save must not make the knob jump back to OFF while the game's lights are still on.
        await StartAsync(_demo.Engine);
        var socket = await ConnectPhoneAsync();
        _ = await ReadAsync(socket, 4);
        await LanRig.SendTextAsync(socket, Select("lights", 2));
        _ = await ReadUntilAsync(socket, f => TypeOf(f) == "state" && PositionOf(f, "lights") == 2);

        _session!.Publish(Fresh(l => Relabel(l, "high-beam", "SAVED")), keepSwitchPositions: true);
        var saved = await ReadFromLayoutAsync(socket);
        Assert.Equal(["layout", "status", "state"], saved.Select(TypeOf));
        Assert.Equal(2, PositionOf(saved[2], "lights"));

        _session.Publish(Fresh(), keepSwitchPositions: false);
        var switched = await ReadFromLayoutAsync(socket);
        Assert.Equal(0, PositionOf(switched[2], "lights"));

        // Switching to a profile whose engine is the one already active (a cached engine) still resets.
        await LanRig.SendTextAsync(socket, Select("lights", 1));
        _ = await ReadUntilAsync(socket, f => TypeOf(f) == "state" && PositionOf(f, "lights") == 1);
        _session.Publish(_session.Engine, keepSwitchPositions: false);
        var again = await ReadFromLayoutAsync(socket);
        Assert.Equal(0, PositionOf(again[2], "lights"));
    }

    // Telemetry from an earlier press may still be on its way: skip to the layout, then take its status and state.
    private static async Task<List<string>> ReadFromLayoutAsync(WebSocket socket)
    {
        var frames = new List<string> { await ReadUntilAsync(socket, f => TypeOf(f) == "layout") };
        frames.AddRange(await ReadAsync(socket, 2));
        return frames;
    }

    private static async Task<string> ReadUntilAsync(WebSocket socket, Func<string, bool> match)
    {
        while (true)
        {
            var frame = await LanRig.ReadFrameAsync(socket);
            if (match(frame))
            {
                return frame;
            }
        }
    }

    // Stands in for a phone's socket where a test needs to run code between GameSession.Open and FrameSender.Start.
    private sealed class RecordingSocket : WebSocket
    {
        private readonly List<string> _frames = [];

        public string[] Frames
        {
            get
            {
                lock (_frames)
                {
                    return [.. _frames];
                }
            }
        }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        public override WebSocketState State => WebSocketState.Open;

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (_frames)
            {
                _frames.Add(Encoding.UTF8.GetString(buffer.Span));
            }

            return ValueTask.CompletedTask;
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Abort()
        {
        }

        public override void Dispose()
        {
        }
    }

    private static FrameSender NewSender(RecordingSocket socket) => new(socket, new SemaphoreSlim(1, 1), () => false, () => { });

    [Fact]
    public async Task A_publish_between_Open_and_Start_is_not_lost_the_phone_ends_on_the_newest_layout()
    {
        // In DeviceConnections a phone joins the fan-out (Open) and then loads its header (Start). A publish in between must not
        // leave it with the layout it resolved at Open.
        var a = _demo.Engine;
        var b = Fresh(l => Relabel(l, "high-beam", "NEWEST"));
        _session = new GameSession(a, _sim, _sim);
        var socket = new RecordingSocket();
        var sender = NewSender(socket);
        try
        {
            using var link = _session.Open(LanFixture.TemplateId, sender);
            _session.Publish(b, keepSwitchPositions: true);
            sender.Start([Encoding.UTF8.GetBytes("""{"type":"welcome"}""")], link.BuildLayout, link.BuildStatus, link.BuildState);

            await LanRig.WaitForAsync(() => socket.Frames.Length, n => n >= 4);
            await Task.Delay(100, Ct);

            var frames = socket.Frames;
            Assert.Equal(["welcome", "layout", "status", "state"], frames.Select(TypeOf));
            Assert.Equal(LayoutFrame(b), frames[1]);
        }
        finally
        {
            await sender.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_state_is_only_built_for_the_layout_the_phone_was_last_sent()
    {
        // The pump builds frames at send time. If a publish lands between "state is next" and "state is built", the state would be
        // for a layout the phone does not have yet. Null (nothing to send now) is right: the publish marked layout and state again.
        _session = new GameSession(_demo.Engine, _sim, _sim);
        var sender = NewSender(new RecordingSocket());
        try
        {
            using var link = _session.Open(LanFixture.TemplateId, sender);
            Assert.Null(link.BuildState()); // no layout sent yet

            Assert.NotNull(link.BuildLayout());
            Assert.NotNull(link.BuildState());

            _session.Publish(Fresh(l => Remove(l, "beacon")), keepSwitchPositions: true);
            Assert.Null(link.BuildState()); // the phone still shows the old layout

            Assert.Equal(LayoutFrame(_session.Engine!), Encoding.UTF8.GetString(link.BuildLayout()!));
            Assert.NotNull(link.BuildState());

            _session.Publish(null, keepSwitchPositions: false);
            _ = link.BuildLayout();
            Assert.Null(link.BuildState()); // noLayout: no state, ever
        }
        finally
        {
            await sender.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_phone_that_connects_in_the_same_instant_as_a_publish_ends_on_the_new_engine()
    {
        // Open resolves the engine and joins the fan-out in one step under the lock Publish swaps under. If the lookup happened
        // before the lock, a phone could resolve the old engine, then miss Publish's walk over the links and keep the old layout
        // until it reconnects. One round = one Open and one Publish released together; afterwards the link must be on the new engine.
        const int Rounds = 4000;
        _session = new GameSession(Fresh(), _sim, _sim);
        var engines = Enumerable.Range(0, Rounds).Select(_ => Fresh()).ToList();
        using var barrier = new Barrier(3);
        PhoneLink? link = null;
        FrameSender? sender = null;

        var opener = Task.Factory.StartNew(() =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                sender = NewSender(new RecordingSocket());
                barrier.SignalAndWait();
                link = _session.Open(LanFixture.TemplateId, sender);
                barrier.SignalAndWait();
                barrier.SignalAndWait();
            }
        }, Ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var publisher = Task.Factory.StartNew(() =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                barrier.SignalAndWait();
                _session.Publish(engines[round], keepSwitchPositions: true);
                barrier.SignalAndWait();
                barrier.SignalAndWait();
            }
        }, Ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var stale = 0;
        for (var round = 0; round < Rounds; round++)
        {
            barrier.SignalAndWait(Ct); // both start
            barrier.SignalAndWait(Ct); // both finished
            if (!ReferenceEquals(link!.View.Layout, engines[round].ForTemplate(LanFixture.TemplateId)))
            {
                stale++;
            }

            link.Dispose();
            await sender!.DisposeAsync();
            barrier.SignalAndWait(Ct); // next round
        }

        await Task.WhenAll(opener, publisher);
        Assert.Equal(0, stale);
    }

    [Fact]
    public async Task After_any_mix_of_publishes_telemetry_and_gestures_the_phone_ends_on_the_current_layout_and_a_state_built_for_it()
    {
        // The phone must never show layout A while the host's state refers to layout B. The two layouts each have a widget the other
        // lacks, so a state frame built for the wrong layout names a widget the phone's layout does not have.
        await StartAsync(Fresh(l => Remove(l, "hazard")));
        var socket = await ConnectPhoneAsync();
        var frames = new List<string>();
        using var stop = new CancellationTokenSource();
        var reader = ReadAllAsync(socket, frames, stop.Token);

        ProfileEngine Variant(int i) => i % 2 == 0
            ? Fresh(l => { Remove(l, "beacon"); Relabel(l, "high-beam", "B" + i); })
            : Fresh(l => { Remove(l, "hazard"); Relabel(l, "high-beam", "A" + i); });

        var deadline = DateTime.UtcNow.AddSeconds(1.5);
        var publisher = Task.Run(() =>
        {
            for (var i = 0; DateTime.UtcNow < deadline; i++)
            {
                _session!.Publish(Variant(i), keepSwitchPositions: i % 3 != 0);
            }
        }, Ct);
        var telemetry = Task.Run(async () =>
        {
            while (DateTime.UtcNow < deadline)
            {
                await _sim.PressAsync(new ScsInput("beacon"), CancellationToken.None);
                await _sim.PressAsync(new ScsInput("flasher4way"), CancellationToken.None);
            }
        }, Ct);
        var gestures = Task.Run(async () =>
        {
            for (var i = 0; DateTime.UtcNow < deadline; i++)
            {
                await LanRig.SendTextAsync(socket, i % 3 == 0 ? Select("lights", i % 2 + 1) : Gesture(i % 3 == 1 ? "beacon" : "hazard", "tap"));
                await Task.Delay(1, Ct);
            }
        }, Ct);
        await Task.WhenAll(publisher, telemetry, gestures);

        // Quiesce: no new frame for a while.
        var count = -1;
        while (true)
        {
            await Task.Delay(400, Ct);
            int now;
            lock (frames)
            {
                now = frames.Count;
            }

            if (now == count)
            {
                break;
            }

            count = now;
        }

        // Checked before the reader is cancelled: cancelling a receive aborts a ClientWebSocket.
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.Single(_rig!.Connections.ConnectedDeviceIds);
        await stop.CancelAsync();
        await reader;
        string[] all;
        lock (frames)
        {
            all = [.. frames];
        }

        // Invariant over the whole run: a state frame only names widgets of the layout the phone received last.
        HashSet<string>? shown = null;
        foreach (var frame in all)
        {
            switch (TypeOf(frame))
            {
                case "layout":
                    shown = LayoutIds(frame);
                    break;
                case "state":
                    Assert.NotNull(shown);
                    Assert.True(StateIds(frame).IsSubsetOf(shown!), "state names a widget the phone's layout does not have");
                    break;
            }
        }

        // At the end: the last layout is the current engine's, and the last state came after it, built for it.
        var currentEngine = _session!.Engine!;
        var lastLayout = Array.FindLastIndex(all, f => TypeOf(f) == "layout");
        var lastState = Array.FindLastIndex(all, f => TypeOf(f) == "state");
        Assert.True(all.Count(f => TypeOf(f) == "layout") >= 5, "the stress must actually have swapped layouts on the phone");
        Assert.True(lastLayout >= 0);
        Assert.Equal(LayoutFrame(currentEngine), all[lastLayout]);
        Assert.True(lastState > lastLayout, "a fresh state must follow the last layout");
        Assert.Equal(currentEngine.ForTemplate(LanFixture.TemplateId)!.Project(_sim.Current).Widgets.Keys.ToHashSet(), StateIds(all[lastState]));
    }

    private static async Task ReadAllAsync(WebSocket socket, List<string> frames, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                lock (frames)
                {
                    frames.Add(Encoding.UTF8.GetString(message.ToArray()));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Done collecting.
        }
    }

    [Fact]
    public async Task TestPress_reaches_the_game_even_while_it_is_not_listening()
    {
        // §11: test mode skips only the "game not listening" check, so the editor can be used with the game paused.
        _session = new GameSession(_demo.Engine, _sim, _sim);
        _sim.GameListening = false;

        Assert.Equal(TestPressResult.Sent, _session.TestPress("high-beam"));

        _ = await LanRig.WaitForAsync(() => _sim.Received.Count, count => count >= 1);
        Assert.Equal(new ReceivedOutput(OutputKind.Press, new ScsInput("hblight")), Assert.Single(_sim.Received));
    }

    [Fact]
    public async Task TestPress_answers_NoActiveProfile_after_the_profile_is_unpublished_and_UnknownAction_for_ids_outside_the_catalog()
    {
        _session = new GameSession(_demo.Engine, _sim, _sim);

        Assert.Equal(TestPressResult.UnknownAction, _session.TestPress("not-an-action"));
        Assert.Equal(TestPressResult.UnknownAction, _session.TestPress("hblight")); // the game-side input name is not a catalog id

        _session.Publish(null, keepSwitchPositions: false);
        Assert.Equal(TestPressResult.NoActiveProfile, _session.TestPress("high-beam"));

        await Task.Delay(100, Ct);
        Assert.Empty(_sim.Received);
    }

    private sealed class BlockingBackend : IOutputBackend
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public Task PressAsync(GameOutput output, CancellationToken cancellationToken = default) => _gate.Task;

        public Task HoldAsync(GameOutput output, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReleaseAsync(GameOutput output, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void TestPress_shares_the_bounded_queue_with_phone_presses_and_drops_when_it_is_full()
    {
        // A stuck game must not make the editor wait: the same queue as phone presses, and a full one answers at once.
        var backend = new BlockingBackend();
        _session = new GameSession(_demo.Engine, backend, _sim);
        try
        {
            var sent = 0;
            var result = TestPressResult.Sent;
            for (var i = 0; i < 100 && result == TestPressResult.Sent; i++)
            {
                result = _session.TestPress("high-beam");
                if (result == TestPressResult.Sent)
                {
                    sent++;
                }
            }

            Assert.Equal(TestPressResult.QueueFull, result);
            // One press may already be inside the stuck backend; the rest fill the queue.
            Assert.InRange(sent, GameSession.OutputQueueCapacity, GameSession.OutputQueueCapacity + 1);
        }
        finally
        {
            backend.Release();
        }
    }
}
