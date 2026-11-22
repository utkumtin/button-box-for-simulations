using System.Net.WebSockets;
using System.Text.Json;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;
using Microsoft.AspNetCore.Http;

namespace ButtonBox.App.Lan;

public sealed class DeviceConnectionsOptions
{
    /// <summary>
    /// Looks a token up. Returns null only when the registry definitively lacks it (close 4401); an exception means an internal
    /// error (close 1011, never 4401). Default: <see cref="DeviceRegistry.Authenticate"/>. Tests inject failures here.
    /// </summary>
    public Func<string, DeviceRecord?>? Authenticate { get; init; }

    /// <summary>Second look after registering, so a revoke that raced the authentication cannot leave a live connection. Default: the registry's device list.</summary>
    public Func<string, bool>? DeviceExists { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public int MaxUnauthenticated { get; init; } = 8;

    public int MaxMessageBytes { get; init; } = 1024;

    public int MessagesPerSecond { get; init; } = 20;
}

/// <summary>
/// The phone WebSocket handler and the table of authenticated connections (decisions §9 "Device session").
/// Every byte from the phone is untrusted: text only, at most <see cref="DeviceConnectionsOptions.MaxMessageBytes"/> assembled,
/// strict schema, hello first, rate limited. In Phase 2 gestures and selections are validated and then dropped.
/// </summary>
public sealed class DeviceConnections : IAsyncDisposable
{
    private const int JsonMaxDepth = 8;
    private const int ChunkSize = 512;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions WireOptions = new(ContractJson.Options) { MaxDepth = JsonMaxDepth };

    private static readonly byte[] WelcomeJson = JsonSerializer.SerializeToUtf8Bytes<HostMessage>(new WelcomeMessage(), ContractJson.Options);

    private readonly DeviceRegistry _registry;
    private readonly DeviceConnectionsOptions _o;
    private readonly Func<string, DeviceRecord?> _authenticate;
    private readonly Func<string, bool> _deviceExists;
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _byDevice = new(StringComparer.Ordinal);
    private readonly HashSet<Connection> _all = [];
    private int _unauthenticated;

    public DeviceConnections(DeviceRegistry registry, DeviceConnectionsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _o = options ?? new DeviceConnectionsOptions();
        _authenticate = _o.Authenticate ?? (token => registry.Authenticate(token));
        _deviceExists = _o.DeviceExists ?? (id => registry.Devices.Any(d => d.Id == id));
        registry.Revoked += OnRevoked;
    }

    /// <summary>Raised when an authenticated connection appears or disappears. Handlers must not throw or block.</summary>
    public event Action? ConnectionsChanged;

    public bool IsConnected(string deviceId)
    {
        lock (_gate)
        {
            return _byDevice.ContainsKey(deviceId);
        }
    }

    public IReadOnlyList<string> ConnectedDeviceIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _byDevice.Keys];
            }
        }
    }

    /// <summary>Handles one <c>/ws</c> request (HTTP/1.1 upgrade or HTTP/2 extended CONNECT). The Origin must already have been checked.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Refused before the upgrade: a flood of silent sockets cannot hold more than this many slots.
        if (Interlocked.Increment(ref _unauthenticated) > _o.MaxUnauthenticated)
        {
            Interlocked.Decrement(ref _unauthenticated);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var connection = new Connection(this);
        lock (_gate)
        {
            _all.Add(connection);
        }

        await connection.RunAsync(context);
    }

    /// <summary>Closes every connection (server restart) and waits, bounded, until their handlers are done.</summary>
    public async Task CloseAllAsync()
    {
        Connection[] connections;
        lock (_gate)
        {
            connections = [.. _all];
        }

        await Task.WhenAll(connections.Select(c => c.RequestCloseAsync((int)WebSocketCloseStatus.EndpointUnavailable)));
        await Task.WhenAll(connections.Select(c => Task.WhenAny(c.Completion, Task.Delay(CloseTimeout))));
    }

    public async ValueTask DisposeAsync()
    {
        _registry.Revoked -= OnRevoked;
        await CloseAllAsync();
    }

    private void OnRevoked(string deviceId)
    {
        Connection? connection;
        lock (_gate)
        {
            _byDevice.TryGetValue(deviceId, out connection);
        }

        // Fire and forget: the registry raises this on the thread that revoked, which must not wait for the phone.
        _ = connection?.RequestCloseAsync(CloseCodes.TokenRejected);
    }

    private void Register(Connection connection, DeviceRecord device)
    {
        Connection? previous;
        lock (_gate)
        {
            _byDevice.TryGetValue(device.Id, out previous);
            _byDevice[device.Id] = connection;
            connection.DeviceId = device.Id;
        }

        _ = previous?.RequestCloseAsync(CloseCodes.Replaced);
        RaiseChanged();
    }

    private void Remove(Connection connection)
    {
        var changed = false;
        lock (_gate)
        {
            _all.Remove(connection);
            if (connection.DeviceId is { } id && _byDevice.TryGetValue(id, out var current) && ReferenceEquals(current, connection))
            {
                _byDevice.Remove(id);
                changed = true;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        try
        {
            ConnectionsChanged?.Invoke();
        }
        catch (Exception)
        {
            // A subscriber's failure must not break the connection handling.
        }
    }

    private static ClientMessage? Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ClientMessage>(bytes, WireOptions);
            return ContractValidator.IsValid(message) ? message : null;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed class Connection(DeviceConnections owner)
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocket? _socket;
        private int _closeCode; // first close request wins; 0 = none yet
        private int _helloSeen;
        private int _countsAsUnauthenticated = 1;
        private ITimer? _helloTimer;

        public string? DeviceId { get; set; }

        public Task Completion => _completed.Task;

        public async Task RunAsync(HttpContext context)
        {
            // Created before the upgrade so a client that connected can never outrun it.
            _helloTimer = owner._o.Time.CreateTimer(static s => ((Connection)s!).OnHelloTimeout(), this, owner._o.HelloTimeout, Timeout.InfiniteTimeSpan);
            WebSocket? socket = null;
            try
            {
                socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
                {
                    KeepAliveInterval = owner._o.KeepAliveInterval,
                    KeepAliveTimeout = owner._o.KeepAliveTimeout,
                });
                Volatile.Write(ref _socket, socket);
                var early = Volatile.Read(ref _closeCode);
                if (early != 0)
                {
                    await SendCloseOutputAsync(socket, early);
                }

                await LoopAsync(socket, context.RequestAborted);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // The phone went away or the server is stopping.
            }
            finally
            {
                await CleanUpAsync(socket);
            }
        }

        public async Task RequestCloseAsync(int code)
        {
            if (Interlocked.CompareExchange(ref _closeCode, code, 0) != 0)
            {
                return;
            }

            var socket = Volatile.Read(ref _socket);
            if (socket is null)
            {
                return; // RunAsync sends it right after the upgrade
            }

            await SendCloseOutputAsync(socket, code);
            // The loop ends when the phone answers with its own close frame; if it never does, cut the connection.
            if (await Task.WhenAny(_completed.Task, Task.Delay(CloseTimeout)) != _completed.Task)
            {
                socket.Abort();
            }
        }

        private void OnHelloTimeout()
        {
            if (Volatile.Read(ref _helloSeen) == 0)
            {
                _ = RequestCloseAsync(CloseCodes.HelloTimeout);
            }
        }

        private async Task LoopAsync(WebSocket socket, CancellationToken aborted)
        {
            var message = new byte[owner._o.MaxMessageBytes];
            var chunk = new byte[ChunkSize];
            TokenBucket? bucket = null;

            while (true)
            {
                var received = await ReceiveMessageAsync(socket, message, chunk, aborted);
                if (received.PeerClosed)
                {
                    return;
                }

                if (received.CloseCode != 0)
                {
                    await CloseFromLoopAsync(socket, received.CloseCode);
                    return;
                }

                if (bucket is not null && !bucket.TryTake())
                {
                    await CloseFromLoopAsync(socket, CloseCodes.RateLimited);
                    return;
                }

                var parsed = Parse(message.AsSpan(0, received.Length));
                if (parsed is null)
                {
                    await CloseFromLoopAsync(socket, CloseCodes.BadMessage);
                    return;
                }

                if (bucket is not null)
                {
                    if (parsed is HelloMessage)
                    {
                        await CloseFromLoopAsync(socket, CloseCodes.BadMessage); // one hello per connection
                        return;
                    }

                    continue; // gesture / select: validated, then dropped until layouts exist (Phase 3)
                }

                if (parsed is not HelloMessage hello)
                {
                    await CloseFromLoopAsync(socket, CloseCodes.BadMessage);
                    return;
                }

                var accepted = await AuthenticateAsync(socket, hello);
                if (!accepted)
                {
                    return;
                }

                bucket = new TokenBucket(owner._o.MessagesPerSecond, owner._o.MessagesPerSecond, owner._o.Time);
            }
        }

        private async Task<bool> AuthenticateAsync(WebSocket socket, HelloMessage hello)
        {
            Volatile.Write(ref _helloSeen, 1);
            _helloTimer?.Dispose();

            DeviceRecord? device;
            try
            {
                device = owner._authenticate(hello.Token);
            }
            catch (Exception)
            {
                // The registry could not answer. That is not "unknown token": the phone must keep its token and retry.
                await CloseFromLoopAsync(socket, (int)WebSocketCloseStatus.InternalServerError);
                return false;
            }

            if (device is null)
            {
                await CloseFromLoopAsync(socket, CloseCodes.TokenRejected);
                return false;
            }

            owner.Register(this, device);
            if (!owner._deviceExists(device.Id))
            {
                // Revoked between the lookup and the registration: the Revoked event found nothing to close.
                await CloseFromLoopAsync(socket, CloseCodes.TokenRejected);
                return false;
            }

            LeaveUnauthenticated();
            await socket.SendAsync(WelcomeJson, WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }

        private async Task<Received> ReceiveMessageAsync(WebSocket socket, byte[] message, byte[] chunk, CancellationToken aborted)
        {
            var total = 0;
            while (true)
            {
                var result = await socket.ReceiveAsync(chunk, aborted);
                switch (result.MessageType)
                {
                    case WebSocketMessageType.Close:
                        return new Received(0, 0, PeerClosed: true);
                    case WebSocketMessageType.Binary:
                        return new Received(CloseCodes.BadMessage, 0, false);
                }

                if (total + result.Count > message.Length)
                {
                    return new Received(CloseCodes.TooBig, 0, false);
                }

                Buffer.BlockCopy(chunk, 0, message, total, result.Count);
                total += result.Count;
                if (result.EndOfMessage)
                {
                    return new Received(0, total, false);
                }
            }
        }

        // The loop is the only reader, so it may use CloseAsync (send close, then wait for the peer's close, bounded).
        private async Task CloseFromLoopAsync(WebSocket socket, int code)
        {
            if (Interlocked.CompareExchange(ref _closeCode, code, 0) != 0)
            {
                return; // another thread already closes with its own code (replaced, revoked, timeout)
            }

            try
            {
                using var cts = new CancellationTokenSource(CloseTimeout);
                await socket.CloseAsync((WebSocketCloseStatus)code, null, cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                socket.Abort();
            }
        }

        private static async Task SendCloseOutputAsync(WebSocket socket, int code)
        {
            try
            {
                using var cts = new CancellationTokenSource(CloseTimeout);
                await socket.CloseOutputAsync((WebSocketCloseStatus)code, null, cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                socket.Abort();
            }
        }

        private async Task CleanUpAsync(WebSocket? socket)
        {
            _helloTimer?.Dispose();
            LeaveUnauthenticated();
            owner.Remove(this);
            if (socket is not null)
            {
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        using var cts = new CancellationTokenSource(CloseTimeout);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
                {
                    socket.Abort();
                }

                socket.Dispose();
            }

            _completed.TrySetResult();
        }

        private void LeaveUnauthenticated()
        {
            if (Interlocked.Exchange(ref _countsAsUnauthenticated, 0) == 1)
            {
                Interlocked.Decrement(ref owner._unauthenticated);
            }
        }

        private readonly record struct Received(int CloseCode, int Length, bool PeerClosed);
    }
}
