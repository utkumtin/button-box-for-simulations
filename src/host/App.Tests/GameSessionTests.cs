using System.Net.WebSockets;
using System.Text.Json;
using ButtonBox.App.Lan;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Simulator;
using Xunit;

namespace ButtonBox.App.Tests;

/// <summary>
/// The phone session end to end: a real LAN server and WebSocket, the bundled demo profile, the Simulator as backend and state.
/// </summary>
public sealed class GameSessionTests : IAsyncDisposable
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

    private async Task<LanRig> StartAsync(IOutputBackend? backend = null, DeviceConnectionsOptions? options = null)
    {
        _session = new GameSession(_demo.Engine, backend ?? _sim, _sim);
        // 20 messages per second of fake time never refills here, so tests that send bursts raise the limit.
        _rig = await LanRig.StartAsync(_fx, options ?? new DeviceConnectionsOptions { Time = _fx.Time, MessagesPerSecond = 10_000 }, session: _session, assets: _demo.Assets);
        return _rig;
    }

    private async Task<WebSocket> ConnectAsync(string token)
    {
        var socket = await _rig!.ConnectAsync();
        _sockets.Add(socket);
        await LanRig.SendTextAsync(socket, LanRig.Hello(token));
        return socket;
    }

    private static string TypeOf(string frame)
    {
        using var doc = JsonDocument.Parse(frame);
        return doc.RootElement.GetProperty("type").GetString()!;
    }

    private static JsonElement Widget(string stateFrame, string id)
    {
        using var doc = JsonDocument.Parse(stateFrame);
        return doc.RootElement.GetProperty("widgets").GetProperty(id).Clone();
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

    private static Task<string> ReadStateAsync(WebSocket socket, Func<string, bool> match) =>
        ReadUntilAsync(socket, f => TypeOf(f) == "state" && match(f));

    private static bool IsLit(string stateFrame, string widget) =>
        Widget(stateFrame, widget).TryGetProperty("lit", out var lit) && lit.GetBoolean();

    private static string Gesture(string widget, string gesture) => $$"""{"type":"gesture","widgetId":"{{widget}}","gesture":"{{gesture}}"}""";

    private static string Select(string widget, int position) => $$"""{"type":"select","widgetId":"{{widget}}","position":{{position}}}""";

    [Fact]
    public async Task A_phone_with_the_demo_template_gets_welcome_layout_status_and_state_in_that_order()
    {
        // The phone draws nothing before it has the layout and shows no state before it knows the widgets, so the order is the contract.
        var rig = await StartAsync();
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var socket = await ConnectAsync(token);

        var frames = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            frames.Add(await LanRig.ReadFrameAsync(socket));
        }

        Assert.Equal(["welcome", "layout", "status", "state"], frames.Select(TypeOf));
        var expectedLayout = JsonSerializer.Serialize<HostMessage>(new LayoutMessage { Layout = _demo.Engine.ForTemplate(LanFixture.TemplateId)!.Layout }, ContractJson.Options);
        Assert.Equal(expectedLayout, frames[1]);
        using (var status = JsonDocument.Parse(frames[2]))
        {
            Assert.True(status.RootElement.GetProperty("telemetry").GetBoolean());
            Assert.True(status.RootElement.GetProperty("gameListening").GetBoolean());
        }

        Assert.Equal(0, Widget(frames[3], "lights").GetProperty("position").GetInt32());
        Assert.False(IsLit(frames[3], "high-beam"));
        Assert.True(rig.Connections.IsConnected(_fx.Registry.Devices.Single().Id));
    }

    [Fact]
    public async Task A_device_whose_template_has_no_layout_gets_noLayout_and_status_but_never_state()
    {
        // §10: "No layout for this device in the active profile: a message says so." A state frame would reference widgets it does not have.
        await StartAsync();
        var (_, token) = _fx.Registry.Add("Tablet", OtherTemplate, false);
        var socket = await ConnectAsync(token);

        Assert.Equal("welcome", TypeOf(await LanRig.ReadFrameAsync(socket)));
        Assert.Equal("noLayout", TypeOf(await LanRig.ReadFrameAsync(socket)));
        Assert.Equal("status", TypeOf(await LanRig.ReadFrameAsync(socket)));

        // Two telemetry changes: each must produce exactly a status frame. A state frame after the first status would show up next.
        _sim.GameListening = false;
        var changed = await LanRig.ReadFrameAsync(socket);
        Assert.Equal("status", TypeOf(changed));
        using (var doc = JsonDocument.Parse(changed))
        {
            Assert.False(doc.RootElement.GetProperty("gameListening").GetBoolean());
        }

        _sim.TelemetryAvailable = false;
        Assert.Equal("status", TypeOf(await LanRig.ReadFrameAsync(socket)));

        // Gestures from a device without a layout are dropped (the game is listening again, so only the missing layout can be the reason).
        _sim.TelemetryAvailable = true;
        _sim.GameListening = true;
        _ = await ReadUntilAsync(socket, f => TypeOf(f) == "status" && f.Contains("\"telemetry\":true", StringComparison.Ordinal) && f.Contains("\"gameListening\":true", StringComparison.Ordinal));
        await LanRig.SendTextAsync(socket, Gesture("high-beam", "tap"));
        await LanRig.SendTextAsync(socket, Select("lights", 1));
        await LanRig.SendTextAsync(socket, "{not json"); // barrier: the loop handles messages in order
        var (rest, close) = await LanRig.ReadUntilCloseAsync(socket);
        Assert.Equal(CloseCodes.BadMessage, close);
        Assert.DoesNotContain(rest, f => TypeOf(f) == "state");
        Assert.Empty(_sim.Received);
    }

    [Fact]
    public async Task A_tap_reaches_the_simulator_and_the_resulting_telemetry_lights_the_widget_on_the_phone()
    {
        await StartAsync();
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var socket = await ConnectAsync(token);
        _ = await ReadStateAsync(socket, _ => true);

        await LanRig.SendTextAsync(socket, Gesture("high-beam", "tap"));

        var lit = await ReadStateAsync(socket, f => IsLit(f, "high-beam"));
        Assert.True(IsLit(lit, "high-beam"));
        var received = Assert.Single(_sim.Received);
        Assert.Equal(new ReceivedOutput(OutputKind.Press, new ScsInput("hblight")), received);
    }

    [Fact]
    public async Task A_select_on_a_switch_pushes_the_new_position_to_every_phone_with_the_same_template()
    {
        // Devices with one template share the commanded position; the second phone must see the change the first one made.
        await StartAsync();
        var (_, tokenA) = _fx.Registry.Add("Phone A", LanFixture.TemplateId, false);
        var (_, tokenB) = _fx.Registry.Add("Phone B", LanFixture.TemplateId, false);
        var a = await ConnectAsync(tokenA);
        var b = await ConnectAsync(tokenB);
        _ = await ReadStateAsync(a, _ => true);
        _ = await ReadStateAsync(b, _ => true);

        await LanRig.SendTextAsync(a, Select("lights", 2));

        var onA = await ReadStateAsync(a, f => Widget(f, "lights").GetProperty("position").GetInt32() == 2);
        var onB = await ReadStateAsync(b, f => Widget(f, "lights").GetProperty("position").GetInt32() == 2);
        Assert.Equal(2, Widget(onA, "lights").GetProperty("position").GetInt32());
        Assert.Equal(2, Widget(onB, "lights").GetProperty("position").GetInt32());
        Assert.Equal(new ReceivedOutput(OutputKind.Press, new ScsInput("lighton")), Assert.Single(_sim.Received));
    }

    [Fact]
    public async Task Stale_or_wrong_gestures_are_dropped_without_closing_and_the_next_valid_one_still_works()
    {
        // §3: a well-formed gesture that fails the layout check (the layout may have changed in flight) is dropped, not closed.
        await StartAsync();
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var socket = await ConnectAsync(token);
        _ = await ReadStateAsync(socket, _ => true);

        await LanRig.SendTextAsync(socket, Gesture("no-such-widget", "tap"));
        await LanRig.SendTextAsync(socket, Gesture("high-beam", "hold")); // a toggle has no hold
        await LanRig.SendTextAsync(socket, Gesture("lights", "tap")); // a switch is selected, not tapped
        await LanRig.SendTextAsync(socket, Select("high-beam", 1)); // a toggle has no positions
        await LanRig.SendTextAsync(socket, Select("lights", 5)); // the demo switch has 3 positions
        await LanRig.SendTextAsync(socket, Gesture("high-beam", "tap"));

        _ = await ReadStateAsync(socket, f => IsLit(f, "high-beam"));
        Assert.Equal(new ReceivedOutput(OutputKind.Press, new ScsInput("hblight")), Assert.Single(_sim.Received));
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task While_the_game_is_not_listening_presses_are_dropped_and_not_queued_for_later()
    {
        // §10: "presses are dropped, never queued". A press made while paused must not fire when the game comes back.
        await StartAsync();
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var socket = await ConnectAsync(token);
        _ = await ReadStateAsync(socket, _ => true);
        _sim.GameListening = false;

        await LanRig.SendTextAsync(socket, Gesture("high-beam", "tap"));
        await LanRig.SendTextAsync(socket, Select("lights", 1));
        // A malformed message is the barrier: the loop handles messages in order, so once it closes, both gestures were seen.
        await LanRig.SendTextAsync(socket, "{not json");
        Assert.Equal(CloseCodes.BadMessage, (await LanRig.ReadUntilCloseAsync(socket)).CloseCode);

        _sim.GameListening = true;
        await Task.Delay(100, Ct);
        Assert.Empty(_sim.Received);
    }

    [Fact]
    public async Task A_malformed_message_still_closes_with_1008_after_the_session_started()
    {
        await StartAsync();
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var socket = await ConnectAsync(token);
        _ = await ReadStateAsync(socket, _ => true);

        await LanRig.SendTextAsync(socket, """{"type":"gesture","widgetId":"high-beam","gesture":"tap","extra":1}""");

        Assert.Equal(CloseCodes.BadMessage, (await LanRig.ReadUntilCloseAsync(socket)).CloseCode);
    }

    [Fact]
    public async Task The_rate_limit_still_closes_with_4429_when_the_gestures_would_have_been_valid()
    {
        await StartAsync(options: new DeviceConnectionsOptions { Time = _fx.Time, MessagesPerSecond = 5 });
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var socket = await ConnectAsync(token);
        _ = await ReadStateAsync(socket, _ => true);

        for (var i = 0; i < 12; i++)
        {
            await LanRig.SendTextAsync(socket, Gesture("high-beam", "tap"));
        }

        Assert.Equal(CloseCodes.RateLimited, (await LanRig.ReadUntilCloseAsync(socket)).CloseCode);
    }

    private sealed class GateBackend : IOutputBackend
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Release() => _gate.TrySetResult();

        // The first press never completes until released (a game that hangs); later ones finish at once.
        public Task PressAsync(GameOutput output, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _calls) == 1 ? _gate.Task : Task.CompletedTask;

        public Task HoldAsync(GameOutput output, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReleaseAsync(GameOutput output, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_backend_that_never_completes_does_not_stall_the_receive_loop_and_the_queue_drops_when_full()
    {
        // A stuck game must not freeze the phone's connection (decisions §3). 41 outputs arrive; one is stuck in the backend, 32 wait
        // in the queue, the rest are dropped.
        var backend = new GateBackend();
        await StartAsync(backend);
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var socket = await ConnectAsync(token);
        _ = await ReadStateAsync(socket, _ => true);
        try
        {
            // The first press must be stuck in the backend before the rest arrive. Otherwise, on a busy machine the consumer may
            // take it off the queue only after the queue filled, one more output fits, and the count is 34 (seen in Phase 4 runs).
            await LanRig.SendTextAsync(socket, Gesture("high-beam", "tap"));
            await LanRig.WaitForAsync(() => backend.Calls, calls => calls == 1);
            for (var i = 1; i < 40; i++)
            {
                await LanRig.SendTextAsync(socket, Gesture("high-beam", "tap"));
            }

            // Needs no backend: the phone still gets its state while the backend hangs on its first press.
            await LanRig.SendTextAsync(socket, Select("lights", 1));
            _ = await ReadStateAsync(socket, f => Widget(f, "lights").GetProperty("position").GetInt32() == 1);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
        }
        finally
        {
            backend.Release();
        }

        await LanRig.WaitForAsync(() => backend.Calls, calls => calls >= GameSession.OutputQueueCapacity + 1);
        await Task.Delay(200, Ct);
        Assert.Equal(GameSession.OutputQueueCapacity + 1, backend.Calls);
    }

    [Fact]
    public async Task A_backend_that_throws_does_not_stop_the_presses_behind_it()
    {
        var calls = 0;
        var backend = new ThrowingFirstBackend(_sim, () => Interlocked.Increment(ref calls));
        await StartAsync(backend);
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var socket = await ConnectAsync(token);
        _ = await ReadStateAsync(socket, _ => true);

        await LanRig.SendTextAsync(socket, Gesture("beacon", "tap")); // this one throws
        await LanRig.SendTextAsync(socket, Gesture("high-beam", "tap"));

        _ = await ReadStateAsync(socket, f => IsLit(f, "high-beam"));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    private sealed class ThrowingFirstBackend(IOutputBackend inner, Func<int> count) : IOutputBackend
    {
        public Task PressAsync(GameOutput output, CancellationToken cancellationToken = default) =>
            count() == 1 ? throw new InvalidOperationException("simulated backend failure") : inner.PressAsync(output, cancellationToken);

        public Task HoldAsync(GameOutput output, CancellationToken cancellationToken = default) => inner.HoldAsync(output, cancellationToken);

        public Task ReleaseAsync(GameOutput output, CancellationToken cancellationToken = default) => inner.ReleaseAsync(output, cancellationToken);
    }

    [Fact]
    public async Task Many_rapid_telemetry_changes_leave_the_phone_with_the_state_of_the_final_snapshot()
    {
        // The phone is not reading while 1001 changes happen, like a slow phone. Whatever it skipped, the last state it gets
        // must be the final one, not a stale one that overtook it.
        await StartAsync();
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var socket = await ConnectAsync(token);
        Assert.Equal("welcome", TypeOf(await LanRig.ReadFrameAsync(socket)));

        for (var i = 0; i < 1001; i++)
        {
            await _sim.PressAsync(new ScsInput("beacon"), Ct); // an odd number of toggles: the beacon ends lit
        }

        string? lastState = null;
        using var idle = new CancellationTokenSource();
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromMilliseconds(750));
            try
            {
                var buffer = new byte[16 * 1024];
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, idle.Token);
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var frame = System.Text.Encoding.UTF8.GetString(message.ToArray());
                if (TypeOf(frame) == "state")
                {
                    lastState = frame;
                }

                idle.CancelAfter(TimeSpan.FromMilliseconds(750));
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Assert.NotNull(lastState);
        Assert.Equal(1, _sim.Current.Fields["truck.light.beacon"]);
        Assert.True(IsLit(lastState, "beacon"));
    }

    [Fact]
    public async Task A_telemetry_change_inside_the_authenticate_and_deviceExists_seams_cannot_jump_ahead_of_welcome_and_layout()
    {
        // A change fired while the connection is being set up (before it joined the fan-out, and again right after it registered)
        // must not put a status or state frame in front of welcome and layout.
        var (device, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var options = new DeviceConnectionsOptions
        {
            Time = _fx.Time,
            Authenticate = t =>
            {
                _sim.GameListening = false;
                return _fx.Registry.Authenticate(t);
            },
            DeviceExists = id =>
            {
                _sim.TelemetryAvailable = false;
                return _fx.Registry.Devices.Any(d => d.Id == id);
            },
        };
        await StartAsync(options: options);

        var socket = await ConnectAsync(token);

        Assert.Equal("welcome", TypeOf(await LanRig.ReadFrameAsync(socket)));
        Assert.Equal("layout", TypeOf(await LanRig.ReadFrameAsync(socket)));
        Assert.Equal("status", TypeOf(await LanRig.ReadFrameAsync(socket)));
        Assert.Equal("state", TypeOf(await LanRig.ReadFrameAsync(socket)));
        Assert.Equal(device.Id, Assert.Single(_rig!.Connections.ConnectedDeviceIds));
    }

    [Fact]
    public async Task Telemetry_changes_racing_the_handshake_never_put_a_frame_in_front_of_welcome_and_layout()
    {
        // Same property under real contention: a thread flips telemetry nonstop while many phones connect.
        await StartAsync();
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        using var stop = new CancellationTokenSource();
        var flipper = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await _sim.PressAsync(new ScsInput("beacon"), CancellationToken.None);
            }
        }, CancellationToken.None);

        try
        {
            for (var i = 0; i < 15; i++)
            {
                var socket = await ConnectAsync(token);
                Assert.Equal("welcome", TypeOf(await LanRig.ReadFrameAsync(socket)));
                Assert.Equal("layout", TypeOf(await LanRig.ReadFrameAsync(socket)));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await flipper;
        }
    }

    [Fact]
    public async Task A_device_revoked_between_lookup_and_registration_gets_4401_and_never_a_layout()
    {
        // A layout is the app's content: a phone whose token was revoked in the handshake window must not receive it.
        var (device, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var options = new DeviceConnectionsOptions
        {
            Time = _fx.Time,
            DeviceExists = id =>
            {
                _fx.Registry.Revoke(id);
                _sim.GameListening = false; // and the telemetry moves in the same window
                return _fx.Registry.Devices.Any(d => d.Id == id);
            },
        };
        await StartAsync(options: options);

        var socket = await ConnectAsync(token);
        var (frames, close) = await LanRig.ReadUntilCloseAsync(socket);

        Assert.Equal(CloseCodes.TokenRejected, close);
        Assert.Empty(frames);
        Assert.False(_rig!.Connections.IsConnected(device.Id));
    }

    [Fact]
    public async Task Revoke_and_replace_while_state_frames_stream_still_deliver_4401_and_4409_without_unobserved_exceptions()
    {
        var unobserved = new List<Exception>();
        // Process-wide event: only exceptions from the connection code count, not leftovers of tests running in parallel.
        void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.ToString().Contains("ButtonBox.App.Lan.", StringComparison.Ordinal))
            {
                unobserved.Add(e.Exception);
            }
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await StartAsync();
            using var stop = new CancellationTokenSource();
            var flipper = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await _sim.PressAsync(new ScsInput("beacon"), CancellationToken.None);
                    await Task.Yield();
                }
            }, CancellationToken.None);

            try
            {
                for (var round = 0; round < 8; round++)
                {
                    var (device, token) = _fx.Registry.Add("Phone " + round, LanFixture.TemplateId, false);

                    // Replace: a second connection of the same device while the first one is streaming.
                    var first = await ConnectAsync(token);
                    _ = await ReadStateAsync(first, _ => true);
                    var firstEnd = LanRig.ReadUntilCloseAsync(first);
                    var second = await ConnectAsync(token);
                    Assert.Equal(CloseCodes.Replaced, (await firstEnd).CloseCode);

                    // Revoke: the replacement is streaming too.
                    _ = await ReadStateAsync(second, _ => true);
                    var secondEnd = LanRig.ReadUntilCloseAsync(second);
                    _fx.Registry.Revoke(device.Id);
                    Assert.Equal(CloseCodes.TokenRejected, (await secondEnd).CloseCode);

                    await LanRig.WaitForAsync(() => _rig!.Connections.IsConnected(device.Id), connected => !connected);
                }
            }
            finally
            {
                await stop.CancelAsync();
                await flipper;
            }

            await _rig!.Connections.CloseAllAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }
}
