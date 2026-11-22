using System.Net.WebSockets;
using System.Text;
using ButtonBox.App.Lan;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;
using Xunit;

namespace ButtonBox.App.Tests;

public sealed class LanWebSocketTests : IAsyncDisposable
{
    private readonly LanFixture _fx = new();
    private LanRig? _rig;
    private readonly string _token;
    private readonly DeviceRecord _device;

    public LanWebSocketTests()
    {
        (_device, _token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
    }

    private static CancellationToken Ct => LanFixture.Ct;

    public async ValueTask DisposeAsync()
    {
        if (_rig is not null)
        {
            await _rig.DisposeAsync();
        }

        await _fx.DisposeAsync();
    }

    private async Task<LanRig> StartAsync(DeviceConnectionsOptions? options = null) =>
        _rig = await LanRig.StartAsync(_fx, options ?? new DeviceConnectionsOptions { Time = _fx.Time });

    private static string Padded(string json, int totalBytes) => json + new string(' ', totalBytes - Encoding.UTF8.GetByteCount(json));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hello_with_a_valid_token_gets_welcome_over_http1_and_http2(bool http2)
    {
        // The phone's Safari uses HTTP/2 extended CONNECT when it can and an HTTP/1.1 upgrade otherwise; both must work.
        var rig = await StartAsync();

        using var socket = await rig.ConnectAsync(http2);
        await LanRig.SendTextAsync(socket, LanRig.Hello(_token));

        Assert.Equal("""{"type":"welcome"}""", await LanRig.ReadTextAsync(socket));
        Assert.True(rig.Connections.IsConnected(_device.Id));
        Assert.Equal([_device.Id], rig.Connections.ConnectedDeviceIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task More_than_4_KiB_over_one_socket_is_fine_because_the_body_limit_does_not_apply_to_websockets(bool http2)
    {
        // MaxRequestBodySize is 4 KiB for the HTTP API. If it also capped the WebSocket stream, a phone would be cut off
        // after a few hundred gestures.
        var rig = await StartAsync();
        using var socket = await rig.ConnectAuthenticatedAsync(_token, http2);

        for (var i = 0; i < 10; i++)
        {
            await LanRig.SendTextAsync(socket, Padded(LanRig.GestureJson, 450));
        }

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
    }

    [Fact]
    public async Task A_message_of_exactly_1024_bytes_is_accepted_and_1025_across_fragments_is_not()
    {
        // The limit applies to the assembled message, so splitting it into frames must not get around it.
        var rig = await StartAsync();
        using var ok = await rig.ConnectAsync();
        await LanRig.SendTextAsync(ok, Padded(LanRig.Hello(_token), 1024));
        Assert.Equal("""{"type":"welcome"}""", await LanRig.ReadTextAsync(ok));

        using var big = await rig.ConnectAsync();
        await big.SendAsync(new byte[600], WebSocketMessageType.Text, endOfMessage: false, Ct);
        await big.SendAsync(new byte[425], WebSocketMessageType.Text, endOfMessage: true, Ct);

        Assert.Equal(CloseCodes.TooBig, await LanRig.ReadCloseCodeAsync(big));
    }

    [Fact]
    public async Task A_binary_frame_closes_with_1008()
    {
        var rig = await StartAsync();
        using var socket = await rig.ConnectAsync();

        await socket.SendAsync(Encoding.UTF8.GetBytes(LanRig.Hello(_token)), WebSocketMessageType.Binary, true, Ct);

        Assert.Equal(CloseCodes.BadMessage, await LanRig.ReadCloseCodeAsync(socket));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"type":"hello"}""")]
    [InlineData("""{"type":"hello","token":"short"}""")]
    [InlineData("""{"type":"hello","token":5}""")]
    [InlineData("""{"type":"launch","token":"x"}""")]
    [InlineData("""{"type":"gesture","widgetId":"w1","gesture":"tap"}""")]
    [InlineData("""{"type":"hello","token":"TOKEN","extra":1}""")]
    [InlineData("""{"type":"hello","token":"TOKEN"} trailing""")]
    [InlineData("""{"type":"hello","token":"TOKEN","x":{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":{"i":1}}}}}}}}}}""")]
    public async Task A_bad_or_non_hello_first_message_closes_with_1008(string message)
    {
        // Untrusted input: garbage, a missing type, an unknown field, trailing data and a first message other than hello
        // are all protocol violations, and none of them may reach the registry.
        var authenticateCalls = 0;
        var rig = await StartAsync(new DeviceConnectionsOptions
        {
            Time = _fx.Time,
            Authenticate = token =>
            {
                Interlocked.Increment(ref authenticateCalls);
                return _fx.Registry.Authenticate(token);
            },
        });
        using var socket = await rig.ConnectAsync();

        await LanRig.SendTextAsync(socket, message.Replace("TOKEN", _token, StringComparison.Ordinal));

        Assert.Equal(CloseCodes.BadMessage, await LanRig.ReadCloseCodeAsync(socket));
        Assert.Equal(0, Volatile.Read(ref authenticateCalls));
    }

    [Fact]
    public async Task An_integer_enum_after_login_closes_with_1008_while_the_string_form_does_not()
    {
        var rig = await StartAsync();
        using var good = await rig.ConnectAuthenticatedAsync(_token);
        await LanRig.SendTextAsync(good, LanRig.GestureJson);
        await LanRig.SendTextAsync(good, """{"type":"select","widgetId":"w1","position":7}""");
        await good.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, good.CloseStatus);

        using var bad = await rig.ConnectAuthenticatedAsync(_token);
        await LanRig.SendTextAsync(bad, """{"type":"gesture","widgetId":"w1","gesture":0}""");
        Assert.Equal(CloseCodes.BadMessage, await LanRig.ReadCloseCodeAsync(bad));
    }

    [Theory]
    [InlineData("""{"type":"select","widgetId":"w1","position":8}""")]
    [InlineData("""{"type":"gesture","widgetId":"W 1","gesture":"tap"}""")]
    [InlineData("""{"type":"gesture","widgetId":"w1","gesture":"swipe"}""")]
    public async Task Out_of_range_values_after_login_close_with_1008(string message)
    {
        var rig = await StartAsync();
        using var socket = await rig.ConnectAuthenticatedAsync(_token);

        await LanRig.SendTextAsync(socket, message);

        Assert.Equal(CloseCodes.BadMessage, await LanRig.ReadCloseCodeAsync(socket));
    }

    [Fact]
    public async Task A_second_hello_closes_with_1008()
    {
        var rig = await StartAsync();
        using var socket = await rig.ConnectAuthenticatedAsync(_token);

        await LanRig.SendTextAsync(socket, LanRig.Hello(_token));

        Assert.Equal(CloseCodes.BadMessage, await LanRig.ReadCloseCodeAsync(socket));
    }

    [Fact]
    public async Task No_hello_within_5_seconds_closes_with_4408_but_a_timely_hello_cancels_the_timer()
    {
        var rig = await StartAsync();
        using var silent = await rig.ConnectAsync();
        using var punctual = await rig.ConnectAsync();

        _fx.Time.Advance(TimeSpan.FromSeconds(4));
        await LanRig.SendTextAsync(punctual, LanRig.Hello(_token));
        Assert.Equal("""{"type":"welcome"}""", await LanRig.ReadTextAsync(punctual));
        _fx.Time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(CloseCodes.HelloTimeout, await LanRig.ReadCloseCodeAsync(silent));
        // The authenticated connection survived the same 6 seconds.
        _fx.Time.Advance(TimeSpan.FromSeconds(30));
        await LanRig.SendTextAsync(punctual, LanRig.GestureJson);
        await punctual.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, punctual.CloseStatus);
    }

    [Fact]
    public async Task The_9th_connection_without_hello_is_refused_and_a_slot_frees_up_when_one_leaves()
    {
        var rig = await StartAsync();
        var sockets = new List<ClientWebSocket>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                sockets.Add(await rig.ConnectAsync());
            }

            await Assert.ThrowsAsync<WebSocketException>(() => rig.ConnectAsync());

            await sockets[0].CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
            var replacement = await LanRig.WaitForAsync(() => TryConnect(rig), s => s is not null);
            sockets.Add(replacement!);
        }
        finally
        {
            foreach (var s in sockets)
            {
                s.Dispose();
            }
        }
    }

    private static ClientWebSocket? TryConnect(LanRig rig)
    {
        try
        {
            return rig.ConnectAsync().GetAwaiter().GetResult();
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Authenticated_connections_do_not_use_up_the_unauthenticated_slots()
    {
        var rig = await StartAsync();
        var sockets = new List<ClientWebSocket>();
        try
        {
            for (var i = 0; i < 10; i++)
            {
                sockets.Add(await rig.ConnectAuthenticatedAsync(_token));
            }
        }
        finally
        {
            foreach (var s in sockets)
            {
                s.Dispose();
            }
        }
    }

    [Fact]
    public async Task Twenty_messages_in_a_second_are_fine_and_the_21st_closes_with_4429()
    {
        var rig = await StartAsync();
        using var fine = await rig.ConnectAuthenticatedAsync(_token);
        for (var i = 0; i < 20; i++)
        {
            await LanRig.SendTextAsync(fine, LanRig.GestureJson);
        }

        await fine.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, fine.CloseStatus);

        using var flood = await rig.ConnectAuthenticatedAsync(_token);
        for (var i = 0; i < 21; i++)
        {
            await LanRig.SendTextAsync(flood, LanRig.GestureJson);
        }

        Assert.Equal(CloseCodes.RateLimited, await LanRig.ReadCloseCodeAsync(flood));
    }

    [Fact]
    public async Task The_rate_limit_refills_over_time()
    {
        var rig = await StartAsync();
        using var socket = await rig.ConnectAuthenticatedAsync(_token);
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 20; i++)
            {
                await LanRig.SendTextAsync(socket, LanRig.GestureJson);
            }

            // Wait until the server has consumed the burst before the clock moves on.
            await Task.Delay(500, Ct);
            _fx.Time.Advance(TimeSpan.FromSeconds(1));
        }

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
    }

    [Fact]
    public async Task An_unknown_token_closes_with_4401()
    {
        var rig = await StartAsync();
        using var socket = await rig.ConnectAsync();
        var stranger = new string('A', 43);

        await LanRig.SendTextAsync(socket, LanRig.Hello(stranger));

        Assert.Equal(CloseCodes.TokenRejected, await LanRig.ReadCloseCodeAsync(socket));
    }

    [Fact]
    public async Task A_registry_failure_closes_with_1011_never_4401()
    {
        // 4401 makes the phone delete its token. An I/O error in the registry must not cost anyone their pairing.
        var rig = await StartAsync(new DeviceConnectionsOptions
        {
            Time = _fx.Time,
            Authenticate = _ => throw new DeviceRegistryException("simulated"),
        });
        using var socket = await rig.ConnectAsync();

        await LanRig.SendTextAsync(socket, LanRig.Hello(_token));

        Assert.Equal(1011, await LanRig.ReadCloseCodeAsync(socket));
        Assert.False(rig.Connections.IsConnected(_device.Id));
    }

    [Fact]
    public async Task A_second_connection_with_the_same_token_replaces_the_first_with_4409()
    {
        var rig = await StartAsync();
        using var first = await rig.ConnectAuthenticatedAsync(_token);

        using var second = await rig.ConnectAuthenticatedAsync(_token, http2: true);

        Assert.Equal(CloseCodes.Replaced, await LanRig.ReadCloseCodeAsync(first));
        Assert.True(rig.Connections.IsConnected(_device.Id));
        await LanRig.SendTextAsync(second, LanRig.GestureJson);
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, second.CloseStatus);
    }

    [Fact]
    public async Task Revoking_a_connected_device_closes_it_immediately_with_4401()
    {
        var rig = await StartAsync();
        var changes = 0;
        rig.Connections.ConnectionsChanged += () => Interlocked.Increment(ref changes);
        using var socket = await rig.ConnectAuthenticatedAsync(_token);

        Assert.True(_fx.Registry.Revoke(_device.Id));

        Assert.Equal(CloseCodes.TokenRejected, await LanRig.ReadCloseCodeAsync(socket));
        await LanRig.WaitForAsync(() => rig.Connections.IsConnected(_device.Id), connected => !connected);
        Assert.True(Volatile.Read(ref changes) >= 2); // appeared, then disappeared
        using var again = await rig.ConnectAsync();
        await LanRig.SendTextAsync(again, LanRig.Hello(_token));
        Assert.Equal(CloseCodes.TokenRejected, await LanRig.ReadCloseCodeAsync(again));
    }

    [Fact]
    public async Task A_revoke_between_authentication_and_registration_still_ends_in_4401()
    {
        // The Revoked event fires when the connection is not in the table yet, so nothing closes it. The re-check after
        // registering is what stops a revoked device from staying connected.
        var rig = await StartAsync(new DeviceConnectionsOptions
        {
            Time = _fx.Time,
            Authenticate = token =>
            {
                var device = _fx.Registry.Authenticate(token);
                if (device is not null)
                {
                    _fx.Registry.Revoke(device.Id);
                }

                return device;
            },
        });
        using var socket = await rig.ConnectAsync();

        await LanRig.SendTextAsync(socket, LanRig.Hello(_token));

        Assert.Equal(CloseCodes.TokenRejected, await LanRig.ReadCloseCodeAsync(socket));
        await LanRig.WaitForAsync(() => rig.Connections.IsConnected(_device.Id), connected => !connected);
    }

    [Fact]
    public async Task CloseAll_closes_every_connection_for_a_server_restart()
    {
        var rig = await StartAsync();
        using var authenticated = await rig.ConnectAuthenticatedAsync(_token);
        using var anonymous = await rig.ConnectAsync();

        var closeA = LanRig.ReadCloseCodeAsync(authenticated);
        var closeB = LanRig.ReadCloseCodeAsync(anonymous);
        await rig.Connections.CloseAllAsync();

        Assert.Equal(1001, await closeA);
        Assert.Equal(1001, await closeB);
        Assert.Empty(rig.Connections.ConnectedDeviceIds);
    }

    [Fact]
    public async Task A_vanished_phone_is_removed_from_the_table()
    {
        var rig = await StartAsync();
        var socket = await rig.ConnectAuthenticatedAsync(_token);
        Assert.True(rig.Connections.IsConnected(_device.Id));

        socket.Abort();
        socket.Dispose();

        await LanRig.WaitForAsync(() => rig.Connections.IsConnected(_device.Id), connected => !connected);
    }
}
