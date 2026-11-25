using System.IO.Pipes;
using System.Security.Principal;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Output.Scs.Pipe;
using ButtonBox.Output.Scs.Protocol;

namespace ButtonBox.Output.Scs;

/// <summary>Why the bridge is not connected (decisions §9 and §10), for the Settings panel and logs.</summary>
public enum ScsBridgeFailure
{
    /// <summary>No failure to report.</summary>
    None,

    /// <summary>No game serves the pipe (game not running, plugin not installed or not loaded yet).</summary>
    PipeNotFound,

    /// <summary>A pipe server answered but failed the server check; see <see cref="ScsBridgeStatus.ServerCheck"/>.</summary>
    ServerCheckFailed,

    /// <summary>The plugin speaks another protocol number ("update the plugin"); see <see cref="ScsBridgeStatus.PluginProtocol"/>.</summary>
    ProtocolMismatch,

    /// <summary>The plugin sent a malformed frame or message, a second or missing hello, or no hello within the timeout.</summary>
    ProtocolError,
}

/// <summary>What the bridge knows about its link. <paramref name="Detail"/> holds paths and numbers only, never secrets.</summary>
/// <param name="Connected">The server check passed and a valid hello arrived; the connection is open.</param>
/// <param name="Game">The game named by the connected plugin's hello.</param>
/// <param name="Failure">Why the last connect attempt failed; None while connected or before the first attempt.</param>
/// <param name="ServerCheck">Which server check failed when <paramref name="Failure"/> is ServerCheckFailed.</param>
/// <param name="PluginProtocol">The plugin's protocol number when <paramref name="Failure"/> is ProtocolMismatch.</param>
public sealed record ScsBridgeStatus(
    bool Connected,
    ScsGame? Game,
    ScsBridgeFailure Failure,
    ServerCheckFailure ServerCheck = ServerCheckFailure.None,
    uint? PluginProtocol = null,
    string? Detail = null)
{
    public static ScsBridgeStatus Initial { get; } = new(false, null, ScsBridgeFailure.None);
}

/// <summary>Timings and the pipe name of <see cref="ScsBridge"/>. The defaults are the decisions §9 values.</summary>
public sealed record ScsBridgeOptions
{
    /// <summary>The pipe name without the <c>\\.\pipe\</c> prefix.</summary>
    public string PipeName { get; init; } = ScsProtocol.PipeName;

    /// <summary>How long one connect attempt waits for the pipe to exist (real time; a pipe call, not a timer).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a connected plugin may take to send a valid hello.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Wait before the next attempt after a failure or a closed connection.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Wait before the next attempt after a protocol-version mismatch, so the host does not spin on an old plugin.</summary>
    public TimeSpan MismatchDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Timeout of every host write, including waiting for an earlier write.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// The host side of the ETS2/ATS link: the output backend (presses to the plugin) and the state provider (telemetry from the
/// plugin) for SCS profiles. The host is the pipe client; it checks the server on every connect before it reads or writes.
/// </summary>
public sealed class ScsBridge : IOutputBackend, IStateProvider, IAsyncDisposable
{
    private readonly Func<IReadOnlyCollection<string>> _allowedExePaths;
    private readonly ScsBridgeOptions _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _startGate = new();
    private readonly Dictionary<string, int> _fields = [];
    private Task? _loop;
    private volatile Connection? _connection;
    private volatile TelemetrySnapshot _current = TelemetrySnapshot.None;
    private volatile ScsBridgeStatus _status = ScsBridgeStatus.Initial;
    private bool _avail;
    private bool _listening;
    private int _disposed;

    /// <param name="allowedExePaths">Read on every connect: the full paths of the game executables that may serve the pipe.</param>
    public ScsBridge(Func<IReadOnlyCollection<string>> allowedExePaths, ScsBridgeOptions? options = null, TimeProvider? timeProvider = null)
    {
        _allowedExePaths = allowedExePaths;
        _options = options ?? new ScsBridgeOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Trusts the executables of the games the locator finds now (read again on every connect).</summary>
    public ScsBridge(IScsGameLocator locator, ScsBridgeOptions? options = null, TimeProvider? timeProvider = null)
        : this(() => locator.Find().Select(install => install.ExePath).ToArray(), options, timeProvider)
    {
    }

    /// <inheritdoc />
    public TelemetrySnapshot Current => _current;

    /// <inheritdoc />
    public event EventHandler<TelemetrySnapshot>? Changed;

    /// <summary>The link state for the Settings panel.</summary>
    public ScsBridgeStatus Status => _status;

    /// <summary>Raised after <see cref="Status"/> changes.</summary>
    public event EventHandler<ScsBridgeStatus>? StatusChanged;

    /// <summary>Begins the background connect loop. Calling it again does nothing.</summary>
    public void Start()
    {
        lock (_startGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            _loop ??= Task.Run(() => RunAsync(_stop.Token));
        }
    }

    /// <summary>
    /// Sends one <c>press</c> frame. Returns at once while not connected. A write that is not done after the write timeout drops
    /// the press and closes the connection (the loop reconnects). Overlapping calls are safe: each waits at most its own timeout.
    /// </summary>
    /// <exception cref="OperationCanceledException">Only when <paramref name="cancellationToken"/> is cancelled.</exception>
    public async Task PressAsync(GameOutput output, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (output is not ScsInput input || !ScsProtocol.IsName(input.Name))
        {
            return;
        }

        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        var frame = ScsProtocol.Frame(ScsProtocol.Encode(new ScsPress(input.Name)));
        using var timeout = new CancellationTokenSource(_options.WriteTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await connection.WriteLock.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (connection.IsClosed)
                {
                    return;
                }

                try
                {
                    await connection.Pipe.WriteAsync(frame, linked.Token).ConfigureAwait(false);
                }
                catch
                {
                    // A write that did not finish may have sent half a frame: the stream is unusable. Close it before the
                    // lock is released, so no waiting press can write behind the half frame; the loop reconnects.
                    connection.Close();
                    throw;
                }
            }
            finally
            {
                connection.WriteLock.Release();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Timeout, closed connection or broken pipe: the press is dropped. A wait for the lock that timed out did not
            // touch the pipe; the write that holds the lock up closes the connection itself.
            if (e is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    /// <summary>SCS inputs are momentary (decisions §5, hold mode "fire"): nothing to hold.</summary>
    public Task HoldAsync(GameOutput output, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>SCS inputs are momentary: nothing to release.</summary>
    public Task ReleaseAsync(GameOutput output, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Stops the connect loop and closes the pipe.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _connection?.Close();
        Task? loop;
        lock (_startGate)
        {
            loop = _loop;
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The loop never faults; disposing must not throw either way.
            }
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var delay = _options.ReconnectDelay;
            try
            {
                delay = await RunSessionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Whatever the pipe APIs throw, the loop must survive and retry.
                SetFailure(ScsBridgeFailure.ProtocolError, detail: e.GetType().Name);
            }

            try
            {
                await Task.Delay(delay, _time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One connection from connect to close. Returns the wait before the next attempt.</summary>
    private async Task<TimeSpan> RunSessionAsync(CancellationToken token)
    {
        // Identification: the server may learn who we are but can never act as us (decisions §9).
        var pipe = new NamedPipeClientStream(".", _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        var connection = new Connection(pipe);
        try
        {
            try
            {
                await pipe.ConnectAsync((int)_options.ConnectTimeout.TotalMilliseconds, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                SetFailure(ScsBridgeFailure.PipeNotFound);
                return _options.ReconnectDelay;
            }
            catch (UnauthorizedAccessException)
            {
                SetFailure(ScsBridgeFailure.ServerCheckFailed, ServerCheckFailure.PipeAccessDenied, detail: "the pipe's DACL excludes the current user");
                return _options.ReconnectDelay;
            }
            catch (IOException)
            {
                SetFailure(ScsBridgeFailure.PipeNotFound);
                return _options.ReconnectDelay;
            }

            ServerCheckResult check;
            try
            {
                check = PipeServerCheck.Check(pipe, _allowedExePaths());
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                check = new ServerCheckResult(ServerCheckFailure.ServerProcessUnknown, e.GetType().Name);
            }

            if (!check.Trusted)
            {
                SetFailure(ScsBridgeFailure.ServerCheckFailed, check.Failure, detail: check.Detail);
                return _options.ReconnectDelay;
            }

            var session = new Session(pipe);
            var hello = await ReadHelloAsync(session, token).ConfigureAwait(false);
            if (hello is null)
            {
                return _options.ReconnectDelay;
            }

            if (hello.Protocol != ScsProtocol.Version)
            {
                SetFailure(ScsBridgeFailure.ProtocolMismatch, pluginProtocol: hello.Protocol, detail: $"plugin protocol {hello.Protocol}, host protocol {ScsProtocol.Version}");
                return _options.MismatchDelay;
            }

            _fields.Clear();
            _avail = false;
            _listening = false;
            _connection = connection;
            SetStatus(new ScsBridgeStatus(true, hello.Game, ScsBridgeFailure.None));
            await ReadLoopAsync(session, token).ConfigureAwait(false);
            return _options.ReconnectDelay;
        }
        finally
        {
            connection.Close();
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
            }

            _fields.Clear();
            _avail = false;
            _listening = false;
            PublishSnapshot();
            if (_status.Connected)
            {
                SetStatus(ScsBridgeStatus.Initial);
            }
        }
    }

    /// <summary>The first message, which must be a valid hello within the timeout; null (and a failure set) otherwise.</summary>
    private async Task<ScsHello?> ReadHelloAsync(Session session, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(_options.HelloTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        byte[]? payload;
        try
        {
            payload = await session.ReadPayloadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            SetFailure(ScsBridgeFailure.ProtocolError, detail: "no hello within the timeout");
            return null;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            SetFailure(ScsBridgeFailure.ProtocolError, detail: "connection closed before hello");
            return null;
        }

        if (payload is null)
        {
            SetFailure(ScsBridgeFailure.ProtocolError, detail: session.BadFrame ? "bad frame length before hello" : "connection closed before hello");
            return null;
        }

        if (ScsProtocol.TryParsePluginMessage(payload, out var message) && message is ScsHello hello)
        {
            return hello;
        }

        SetFailure(ScsBridgeFailure.ProtocolError, detail: "the first message is not a valid hello");
        return null;
    }

    private async Task ReadLoopAsync(Session session, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var payload = await session.ReadPayloadAsync(token).ConfigureAwait(false);
                if (payload is null)
                {
                    if (session.BadFrame)
                    {
                        SetFailure(ScsBridgeFailure.ProtocolError, detail: "bad frame length");
                    }

                    return;
                }

                if (!ScsProtocol.TryParsePluginMessage(payload, out var message) || message is ScsHello)
                {
                    SetFailure(ScsBridgeFailure.ProtocolError, detail: "malformed message or a second hello");
                    return;
                }

                Apply(message);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The plugin went away, or a press write closed the connection: reconnect.
        }
    }

    private void Apply(ScsPluginMessage message)
    {
        switch (message)
        {
            case ScsTel tel:
                _fields[tel.Field] = tel.Value;
                break;
            case ScsAvail avail:
                _avail = avail.Available;
                break;
            case ScsListening listening:
                _listening = listening.Listening;
                break;
        }

        PublishSnapshot();
    }

    /// <summary>Builds the snapshot and raises Changed when it differs from the current one.</summary>
    private void PublishSnapshot()
    {
        var snapshot = _avail
            ? new TelemetrySnapshot(true, _listening, new Dictionary<string, int>(_fields))
            : TelemetrySnapshot.None;
        var old = _current;
        if (old.Available == snapshot.Available
            && old.GameListening == snapshot.GameListening
            && old.Fields.Count == snapshot.Fields.Count
            && snapshot.Fields.All(pair => old.Fields.TryGetValue(pair.Key, out var value) && value == pair.Value))
        {
            return;
        }

        _current = snapshot;
        foreach (var handler in (Changed?.GetInvocationList() ?? []).Cast<EventHandler<TelemetrySnapshot>>())
        {
            try
            {
                handler(this, snapshot);
            }
            catch (Exception)
            {
                // A faulty subscriber must not break the read loop.
            }
        }
    }

    private void SetFailure(ScsBridgeFailure failure, ServerCheckFailure serverCheck = ServerCheckFailure.None, uint? pluginProtocol = null, string? detail = null) =>
        SetStatus(new ScsBridgeStatus(false, null, failure, serverCheck, pluginProtocol, detail));

    private void SetStatus(ScsBridgeStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        foreach (var handler in (StatusChanged?.GetInvocationList() ?? []).Cast<EventHandler<ScsBridgeStatus>>())
        {
            try
            {
                handler(this, status);
            }
            catch (Exception)
            {
                // A faulty subscriber must not break the connect loop.
            }
        }
    }

    /// <summary>A verified, hello-checked pipe with its write lock; closing it is idempotent and wakes the read loop.</summary>
    private sealed class Connection(NamedPipeClientStream pipe)
    {
        private int _closed;

        public NamedPipeClientStream Pipe { get; } = pipe;

        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        public bool IsClosed => Volatile.Read(ref _closed) == 1;

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Pipe.Dispose();
            }
        }
    }

    /// <summary>Reads whole frames from the pipe; a bad frame length is a protocol error.</summary>
    private sealed class Session(NamedPipeClientStream pipe)
    {
        private readonly FrameReader _reader = new();
        private readonly byte[] _chunk = new byte[FrameReader.MaxChunk];

        /// <summary>True when the last null from <see cref="ReadPayloadAsync"/> was a bad frame length rather than a closed pipe.</summary>
        public bool BadFrame { get; private set; }

        /// <summary>The next payload, or null when the plugin closed the pipe or sent a bad frame length.</summary>
        public async Task<byte[]?> ReadPayloadAsync(CancellationToken token)
        {
            while (true)
            {
                switch (_reader.TryRead(out var payload))
                {
                    case FrameReadResult.Frame:
                        return payload;
                    case FrameReadResult.Error:
                        BadFrame = true;
                        return null;
                }

                var read = await pipe.ReadAsync(_chunk, token).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                _reader.Push(_chunk.AsSpan(0, read));
            }
        }
    }
}
