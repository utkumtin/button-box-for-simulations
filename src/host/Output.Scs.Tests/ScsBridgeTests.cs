using System.Security.Principal;
using System.Text;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Output.Scs.Pipe;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ButtonBox.Output.Scs.Tests;

// Why: the bridge is the only path between a phone tap and the game, and the only source of "is the game ready" for the
// button box (decisions §3, §9, §10). It must trust only the real game, never block the receive loop or later presses, and
// recover by itself when the game starts, stops or restarts.
public class ScsBridgeTests
{
    private static readonly string OwnImage = Environment.ProcessPath!;

    private static ScsBridgeOptions Options(string pipe, TimeSpan? hello = null) => new()
    {
        PipeName = pipe,
        ConnectTimeout = TimeSpan.FromMilliseconds(50),
        HelloTimeout = hello ?? TimeSpan.FromHours(1),
    };

    private static ScsBridge NewBridge(string pipe, FakeTimeProvider time, string[]? allowed = null, TimeSpan? hello = null) =>
        new(() => allowed ?? [OwnImage], Options(pipe, hello), time);

    [Fact]
    public async Task Snapshot_then_changes_become_Current_and_raise_Changed()
    {
        using var plugin = new FakePlugin();
        var time = new FakeTimeProvider();
        await using var bridge = NewBridge(plugin.Name, time);
        var changes = new List<TelemetrySnapshot>();
        bridge.Changed += (_, s) => changes.Add(s);
        bridge.Start();

        await plugin.WaitForClientAsync();
        await plugin.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current is { Available: true, GameListening: true }, "snapshot");

        Assert.Equal(1, bridge.Current.Fields["truck.engine.enabled"]);
        Assert.Equal(2, bridge.Current.Fields["truck.wipers"]);
        Assert.True(bridge.Status.Connected);
        Assert.Equal(ScsGame.Ets2, bridge.Status.Game);

        var before = changes.Count;
        await plugin.SendAsync("tel truck.wipers 3");
        await plugin.SendAsync("tel truck.some.future.channel -5");
        await Wait.UntilAsync(() => bridge.Current.Fields.ContainsKey("truck.some.future.channel"), "tel change");
        Assert.Equal(3, bridge.Current.Fields["truck.wipers"]);
        Assert.Equal(-5, bridge.Current.Fields["truck.some.future.channel"]);
        Assert.Equal(before + 2, changes.Count);
        Assert.Equal(3, changes[^2].Fields["truck.wipers"]);
    }

    [Fact]
    public async Task Avail_zero_makes_the_snapshot_empty_and_listening_zero_keeps_the_fields()
    {
        using var plugin = new FakePlugin();
        await using var bridge = NewBridge(plugin.Name, new FakeTimeProvider());
        bridge.Start();
        await plugin.WaitForClientAsync();
        await plugin.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current.GameListening, "snapshot");

        await plugin.SendAsync("listening 0");
        await Wait.UntilAsync(() => !bridge.Current.GameListening, "listening 0");
        Assert.True(bridge.Current.Available);
        Assert.Equal(2, bridge.Current.Fields.Count);

        await plugin.SendAsync("listening 1");
        await Wait.UntilAsync(() => bridge.Current.GameListening, "listening 1");
        await plugin.SendAsync("avail 0");
        await Wait.UntilAsync(() => !bridge.Current.Available, "avail 0");
        Assert.False(bridge.Current.GameListening);
        Assert.Empty(bridge.Current.Fields);
        Assert.True(bridge.Status.Connected);

        // Values that arrive while the game has no truck are not shown, and the old ones are not resurrected stale.
        await plugin.SendAsync("tel truck.wipers 1");
        await plugin.SendAsync("avail 1");
        await Wait.UntilAsync(() => bridge.Current.Available, "avail 1");
        Assert.Equal(1, bridge.Current.Fields["truck.wipers"]);
    }

    [Fact]
    public async Task A_throwing_Changed_handler_does_not_break_the_read_loop()
    {
        using var plugin = new FakePlugin();
        await using var bridge = NewBridge(plugin.Name, new FakeTimeProvider());
        var second = 0;
        bridge.Changed += (_, _) => throw new InvalidOperationException("subscriber bug");
        bridge.Changed += (_, _) => Interlocked.Increment(ref second);
        bridge.Start();
        await plugin.WaitForClientAsync();
        await plugin.SendSnapshotAsync();
        await plugin.SendAsync("tel truck.wipers 3");
        await Wait.UntilAsync(() => bridge.Current.Fields.GetValueOrDefault("truck.wipers") == 3, "change after a throwing handler");
        Assert.True(Volatile.Read(ref second) >= 2);
        Assert.True(bridge.Status.Connected);
    }

    [Fact]
    public async Task No_hello_within_the_timeout_disconnects_and_reconnects_after_one_second()
    {
        using var plugin = new FakePlugin();
        var time = new FakeTimeProvider();
        await using var bridge = NewBridge(plugin.Name, time, hello: TimeSpan.FromSeconds(2));
        bridge.Start();
        await plugin.WaitForClientAsync();
        await Task.Delay(150, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMilliseconds(1999));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(ScsBridgeFailure.None, bridge.Status.Failure);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await Wait.UntilAsync(() => bridge.Status.Failure == ScsBridgeFailure.ProtocolError, "hello timeout");
        Assert.Contains("hello", bridge.Status.Detail);
        Assert.True(await plugin.SawEndOfStreamAsync());
        await AssertReconnectsAfterAsync(plugin, time, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_plugin_with_another_protocol_number_is_refused_and_retried_only_every_five_seconds()
    {
        using var plugin = new FakePlugin();
        var time = new FakeTimeProvider();
        await using var bridge = NewBridge(plugin.Name, time);
        bridge.Start();
        await plugin.WaitForClientAsync();
        await plugin.SendAsync("hello 2 ets2 9.0.0");
        await plugin.SendAsync("avail 1");

        await Wait.UntilAsync(() => bridge.Status.Failure == ScsBridgeFailure.ProtocolMismatch, "mismatch");
        Assert.Equal(2u, bridge.Status.PluginProtocol);
        Assert.False(bridge.Status.Connected);
        Assert.False(bridge.Current.Available);
        Assert.True(await plugin.SawEndOfStreamAsync());
        await AssertReconnectsAfterAsync(plugin, time, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("first message is not hello", "avail 1", null)]
    [InlineData("hello twice", "hello 1 ets2 1.0.0", "hello 1 ets2 1.0.0")]
    [InlineData("unknown command", "hello 1 ets2 1.0.0", "evt paused")]
    [InlineData("bad grammar", "hello 1 ets2 1.0.0", "tel Truck.Wipers 1")]
    [InlineData("press sent to the host", "hello 1 ets2 1.0.0", "press lighton")]
    public async Task A_malformed_or_misplaced_message_closes_the_connection_and_reconnects_after_one_second(string why, string first, string? second)
    {
        using var plugin = new FakePlugin();
        var time = new FakeTimeProvider();
        await using var bridge = NewBridge(plugin.Name, time);
        bridge.Start();
        await plugin.WaitForClientAsync();
        await plugin.SendAsync(first);
        await plugin.SendAsync("avail 1");
        if (second is not null)
        {
            await plugin.SendAsync(second);
        }

        await Wait.UntilAsync(() => bridge.Status.Failure == ScsBridgeFailure.ProtocolError, why);
        Assert.False(bridge.Status.Connected);
        Assert.Equal(TelemetrySnapshot.None.Available, bridge.Current.Available);
        Assert.True(await plugin.SawEndOfStreamAsync());
        await AssertReconnectsAfterAsync(plugin, time, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("0000")]
    [InlineData("0101")]
    public async Task A_bad_frame_length_closes_the_connection(string hex)
    {
        using var plugin = new FakePlugin();
        var time = new FakeTimeProvider();
        await using var bridge = NewBridge(plugin.Name, time);
        bridge.Start();
        await plugin.WaitForClientAsync();
        await plugin.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current.Available, "snapshot");

        await plugin.SendRawAsync(Convert.FromHexString(hex));
        await Wait.UntilAsync(() => bridge.Status.Failure == ScsBridgeFailure.ProtocolError && !bridge.Current.Available, "bad frame");
        Assert.Empty(bridge.Current.Fields);
        Assert.True(await plugin.SawEndOfStreamAsync());
    }

    [Fact]
    public async Task Reconnects_by_itself_when_the_server_goes_away_and_comes_back()
    {
        var name = FakePlugin.NewName();
        var time = new FakeTimeProvider();
        await using var bridge = NewBridge(name, time);
        bridge.Start();

        // No game yet: the bridge keeps retrying and says the pipe was not found.
        await Wait.PumpUntilAsync(time, () => bridge.Status.Failure == ScsBridgeFailure.PipeNotFound, "pipe not found");

        var first = new FakePlugin(name);
        var firstAccept = first.WaitForClientAsync();
        await Wait.PumpUntilAsync(time, () => firstAccept.IsCompleted, "first connect");
        await first.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current.Available, "first session");
        first.Dispose();
        await Wait.UntilAsync(() => !bridge.Current.Available && !bridge.Status.Connected, "server gone");

        using var second = new FakePlugin(name);
        var secondAccept = second.WaitForClientAsync();
        await Wait.PumpUntilAsync(time, () => secondAccept.IsCompleted, "second connect");
        await second.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current.Available, "second session");
        Assert.Equal(ScsGame.Ets2, bridge.Status.Game);
    }

    [Fact]
    public async Task Does_not_trust_or_read_a_server_that_is_not_the_game()
    {
        using var plugin = new FakePlugin();
        var time = new FakeTimeProvider();
        await using var bridge = NewBridge(plugin.Name, time, allowed: [@"C:\Games\Euro Truck Simulator 2\bin\win_x64\eurotrucks2.exe"]);
        bridge.Start();
        await plugin.WaitForClientAsync();
        await plugin.SendSnapshotAsync();

        await Wait.UntilAsync(() => bridge.Status.Failure == ScsBridgeFailure.ServerCheckFailed, "server check");
        Assert.Equal(ServerCheckFailure.ImageNotAllowed, bridge.Status.ServerCheck);
        Assert.False(bridge.Status.Connected);
        Assert.Null(bridge.Status.Game);
        Assert.False(bridge.Current.Available);
        Assert.Empty(bridge.Current.Fields);

        // Presses are dropped, and the rejected server receives nothing at all, not even a close-before-data surprise.
        await bridge.PressAsync(new ScsInput("lighton"), TestContext.Current.CancellationToken);
        Assert.True(await plugin.SawEndOfStreamAsync());
    }

    [Fact]
    public async Task The_allowed_paths_are_read_again_on_every_connect()
    {
        using var plugin = new FakePlugin();
        var time = new FakeTimeProvider();
        string[] allowed = [@"C:\wrong\game.exe"];
        await using var bridge = new ScsBridge(() => allowed, Options(plugin.Name), time);
        bridge.Start();
        await plugin.WaitForClientAsync();
        await plugin.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Status.Failure == ScsBridgeFailure.ServerCheckFailed, "first rejection");
        Assert.True(await plugin.SawEndOfStreamAsync());

        allowed = [OwnImage];
        plugin.Disconnect();
        var accept = plugin.WaitForClientAsync();
        await Wait.PumpUntilAsync(time, () => accept.IsCompleted, "second connect");
        await plugin.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current.Available, "accepted after the game was found");
    }

    [Fact]
    public async Task A_press_arrives_as_one_exact_frame_and_other_outputs_are_ignored()
    {
        using var plugin = new FakePlugin();
        await using var bridge = NewBridge(plugin.Name, new FakeTimeProvider());
        bridge.Start();
        await plugin.WaitForClientAsync();
        await plugin.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current.Available, "connected");

        await bridge.PressAsync(new VJoyButton(3), TestContext.Current.CancellationToken);
        await bridge.PressAsync(new ScsInput("Not A Name"), TestContext.Current.CancellationToken);
        await bridge.HoldAsync(new ScsInput("lighton"), TestContext.Current.CancellationToken);
        await bridge.ReleaseAsync(new ScsInput("lighton"), TestContext.Current.CancellationToken);
        await bridge.PressAsync(new ScsInput("lighton"), TestContext.Current.CancellationToken);

        var raw = new byte[2 + "press lighton".Length];
        var got = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (got < raw.Length)
        {
            got += await plugin.Server.ReadAsync(raw.AsMemory(got), timeout.Token);
        }

        Assert.Equal([0x0D, 0x00, .. Encoding.ASCII.GetBytes("press lighton")], raw);

        // Nothing else was sent: the next press is the next frame.
        await bridge.PressAsync(new ScsInput("wipers2"), TestContext.Current.CancellationToken);
        Assert.Equal("press wipers2", await plugin.ReadPayloadAsync());
    }

    [Fact]
    public async Task A_press_while_not_connected_returns_at_once_and_is_never_queued()
    {
        var name = FakePlugin.NewName();
        var time = new FakeTimeProvider();
        await using var bridge = NewBridge(name, time);

        var beforeStart = bridge.PressAsync(new ScsInput("lighton"), TestContext.Current.CancellationToken);
        Assert.True(beforeStart.IsCompletedSuccessfully);

        bridge.Start();
        var whileSearching = bridge.PressAsync(new ScsInput("lighton"), TestContext.Current.CancellationToken);
        Assert.True(whileSearching.IsCompletedSuccessfully);

        // The game starts later: the old presses must not show up.
        using var plugin = new FakePlugin(name);
        var accept = plugin.WaitForClientAsync();
        await Wait.PumpUntilAsync(time, () => accept.IsCompleted, "connect");
        await plugin.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current.Available, "connected");
        await bridge.PressAsync(new ScsInput("wipers1"), TestContext.Current.CancellationToken);
        Assert.Equal("press wipers1", await plugin.ReadPayloadAsync());
    }

    [Fact]
    public async Task A_cancelled_caller_gets_OperationCanceledException_and_nothing_is_sent()
    {
        using var plugin = new FakePlugin();
        await using var bridge = NewBridge(plugin.Name, new FakeTimeProvider());
        bridge.Start();
        await plugin.WaitForClientAsync();
        await plugin.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current.Available, "connected");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.PressAsync(new ScsInput("lighton"), cts.Token));
        await bridge.PressAsync(new ScsInput("wipers1"), TestContext.Current.CancellationToken);
        Assert.Equal("press wipers1", await plugin.ReadPayloadAsync());
    }

    [Fact]
    public async Task A_server_that_stops_reading_times_out_after_250_ms_drops_the_press_and_the_connection_is_reopened()
    {
        using var plugin = new FakePlugin(inBufferSize: 256);
        var time = new FakeTimeProvider();
        await using var bridge = NewBridge(plugin.Name, time);
        bridge.Start();
        await plugin.WaitForClientAsync();
        await plugin.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current.Available, "connected");

        // The plugin never reads. Presses fill the pipe buffer, then one write cannot complete.
        Task? stuck = null;
        for (var i = 0; i < 20000 && stuck is null; i++)
        {
            var press = bridge.PressAsync(new ScsInput("lighton"), TestContext.Current.CancellationToken);
            if (!press.IsCompleted)
            {
                await Task.WhenAny(press, Task.Delay(20, TestContext.Current.CancellationToken));
            }

            if (!press.IsCompleted)
            {
                stuck = press;
            }
        }

        Assert.NotNull(stuck);
        var started = time.GetUtcNow();

        // A second, overlapping press starts 100 ms (fake) later. It must not wait longer than its own 250 ms.
        time.Advance(TimeSpan.FromMilliseconds(100));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        var secondStarted = time.GetUtcNow();
        var second = bridge.PressAsync(new ScsInput("wipers2"), TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        DateTimeOffset? stuckDone = null;
        DateTimeOffset? secondDone = null;
        await Wait.PumpUntilAsync(
            time,
            () =>
            {
                stuckDone ??= stuck.IsCompleted ? time.GetUtcNow() : null;
                secondDone ??= second.IsCompleted ? time.GetUtcNow() : null;
                return stuckDone is not null && secondDone is not null;
            },
            "both presses finish",
            TimeSpan.FromMilliseconds(10));

        await stuck;
        await second;
        Assert.InRange((stuckDone!.Value - started).TotalMilliseconds, 250, 330);
        Assert.InRange((secondDone!.Value - secondStarted).TotalMilliseconds, 0, 260);

        // The connection was closed on the timeout and the bridge reconnects to the same plugin instance.
        await Wait.UntilAsync(() => !bridge.Status.Connected && !bridge.Current.Available, "connection closed");

        // Everything the plugin can still read is whole frames of the presses that fit; nothing was written behind the
        // abandoned write, in particular not the overlapping press that was waiting for the lock.
        var received = new List<string>();
        while (await plugin.ReadPayloadAsync() is { } payload)
        {
            received.Add(payload);
        }

        Assert.NotEmpty(received);
        Assert.All(received, p => Assert.Equal("press lighton", p));

        plugin.Disconnect();
        var accept = plugin.WaitForClientAsync();
        await Wait.PumpUntilAsync(time, () => accept.IsCompleted, "reconnect");
        await plugin.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current.Available, "reconnected");
        await bridge.PressAsync(new ScsInput("wipers3"), TestContext.Current.CancellationToken);
        Assert.Equal("press wipers3", await plugin.ReadPayloadAsync());
    }

    [Fact]
    public async Task The_client_connects_with_identification_impersonation_only()
    {
        using var plugin = new FakePlugin();
        await using var bridge = NewBridge(plugin.Name, new FakeTimeProvider());
        bridge.Start();
        await plugin.WaitForClientAsync();
        await plugin.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current.Available, "connected");
        await bridge.PressAsync(new ScsInput("lighton"), TestContext.Current.CancellationToken);
        Assert.Equal("press lighton", await plugin.ReadPayloadAsync());

        // A server can impersonate only after it has read from the client; the level it gets is what the client allowed.
        System.Security.Principal.TokenImpersonationLevel? level = null;
        plugin.Server.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(true)!;
            level = identity.ImpersonationLevel;
        });

        Assert.Equal(System.Security.Principal.TokenImpersonationLevel.Identification, level);
    }

    [Fact]
    public async Task Dispose_stops_the_loop_closes_the_pipe_and_completes_promptly()
    {
        using var plugin = new FakePlugin();
        var bridge = NewBridge(plugin.Name, new FakeTimeProvider());
        bridge.Start();
        await plugin.WaitForClientAsync();
        await plugin.SendSnapshotAsync();
        await Wait.UntilAsync(() => bridge.Current.Available, "connected");

        var dispose = bridge.DisposeAsync().AsTask();
        Assert.Same(dispose, await Task.WhenAny(dispose, Task.Delay(3000, TestContext.Current.CancellationToken)));
        Assert.True(await plugin.SawEndOfStreamAsync());
        Assert.False(bridge.Current.Available);
        await bridge.PressAsync(new ScsInput("lighton"), TestContext.Current.CancellationToken);

        // Also while waiting out the reconnect delay, with the clock never advanced.
        var idle = NewBridge(FakePlugin.NewName(), new FakeTimeProvider());
        idle.Start();
        await Wait.UntilAsync(() => idle.Status.Failure == ScsBridgeFailure.PipeNotFound, "searching");
        var idleDispose = idle.DisposeAsync().AsTask();
        Assert.Same(idleDispose, await Task.WhenAny(idleDispose, Task.Delay(3000, TestContext.Current.CancellationToken)));
    }

    /// <summary>The server end is back to listening; the bridge must not connect before <paramref name="delay"/> has passed.</summary>
    private static async Task AssertReconnectsAfterAsync(FakePlugin plugin, FakeTimeProvider time, TimeSpan delay)
    {
        plugin.Disconnect();
        var accept = plugin.WaitForClientAsync();
        await Task.Delay(150, TestContext.Current.CancellationToken);
        time.Advance(delay - TimeSpan.FromMilliseconds(1));
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.False(accept.IsCompleted, "the bridge reconnected before the delay was over");
        time.Advance(TimeSpan.FromMilliseconds(1));
        await accept;
    }
}
