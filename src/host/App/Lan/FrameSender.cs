using System.Net.WebSockets;
using System.Threading.Channels;

namespace ButtonBox.App.Lan;

/// <summary>
/// The one writer of data frames on a phone socket. Frames go out from fixed-order slots: the header (welcome, then layout or
/// noLayout), then the latest status, then the latest state. Status and state are flags, not queues: the frame is built when
/// its turn comes, from the then-current snapshot, so a slow phone gets the newest value and never a backlog. The slots are only
/// flushed after <see cref="Start"/> has loaded the header, so an early telemetry change can never jump ahead of welcome or layout.
/// The marking methods never block and never touch the socket; they are safe on any thread.
/// </summary>
internal sealed class FrameSender : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock;
    private readonly Func<bool> _closing;
    private readonly Action _onFault;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel = new();

    // Capacity 1 and dropping is right here: the channel is only a wake-up, the flags below hold the state.
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private readonly Task _pump;
    private IReadOnlyList<byte[]>? _header;
    private int _headerNext;
    private Func<byte[]>? _buildStatus;
    private Func<byte[]>? _buildState;
    private bool _statusDirty;
    private bool _stateDirty;
    private bool _stopped;

    /// <param name="socket">The phone's socket.</param>
    /// <param name="sendLock">Shared with the connection's close paths: a close frame and a data frame never overlap.</param>
    /// <param name="closing">True once any close was requested; no data frame starts after that.</param>
    /// <param name="onFault">Called when building a frame throws (a host bug). Socket failures are the receive loop's business.</param>
    public FrameSender(WebSocket socket, SemaphoreSlim sendLock, Func<bool> closing, Action onFault)
    {
        _socket = socket;
        _sendLock = sendLock;
        _closing = closing;
        _onFault = onFault;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>
    /// Loads the header and releases the slots. Status is marked, and state too when <paramref name="buildState"/> is given,
    /// so the phone's first frames after the header are always a fresh snapshot.
    /// </summary>
    public void Start(IReadOnlyList<byte[]> header, Func<byte[]>? buildStatus, Func<byte[]>? buildState)
    {
        lock (_gate)
        {
            if (_header is not null)
            {
                throw new InvalidOperationException("Already started.");
            }

            _header = header;
            _buildStatus = buildStatus;
            _buildState = buildState;
            _statusDirty = buildStatus is not null;
            _stateDirty = buildState is not null;
        }

        Wake();
    }

    public void MarkStatus()
    {
        lock (_gate)
        {
            _statusDirty = true;
        }

        Wake();
    }

    public void MarkState()
    {
        lock (_gate)
        {
            _stateDirty = true;
        }

        Wake();
    }

    /// <summary>Stops flushing, waits (bounded) for a send in progress, then cancels it. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopped = true;
        }

        _wake.Writer.TryComplete();
        try
        {
            await _pump.WaitAsync(StopTimeout);
        }
        catch (TimeoutException)
        {
            // A phone that does not read: cut the send.
            await _cancel.CancelAsync();
            await _pump.WaitAsync(StopTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private void Wake() => _wake.Writer.TryWrite(true);

    // Under the lock only flags change; the frame itself is built outside it.
    private Func<byte[]>? NextFrame()
    {
        lock (_gate)
        {
            if (_stopped || _header is null)
            {
                return null;
            }

            if (_headerNext < _header.Count)
            {
                var frame = _header[_headerNext++];
                return () => frame;
            }

            if (_statusDirty)
            {
                _statusDirty = false;
                return _buildStatus;
            }

            if (_stateDirty)
            {
                _stateDirty = false;
                return _buildState;
            }

            return null;
        }
    }

    private async Task PumpAsync()
    {
        var token = _cancel.Token;
        try
        {
            while (await _wake.Reader.WaitToReadAsync(token))
            {
                _wake.Reader.TryRead(out _);
                while (NextFrame() is { } build)
                {
                    byte[] frame;
                    try
                    {
                        frame = build();
                    }
                    catch (Exception)
                    {
                        _onFault(); // a host bug, not a phone problem: the connection is closed with 1011
                        return;
                    }

                    if (!await SendAsync(frame, token))
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The phone went away, the connection is closing or the send was cut; the receive loop ends the connection.
        }
        catch (Exception)
        {
            _onFault(); // never an unobserved task exception
        }
    }

    private async Task<bool> SendAsync(byte[] frame, CancellationToken token)
    {
        await _sendLock.WaitAsync(token);
        try
        {
            // Checked under the lock: a close that set its code first holds or is waiting for this lock, so nothing starts after it.
            if (_closing() || _socket.State != WebSocketState.Open)
            {
                return false;
            }

            await _socket.SendAsync(frame, WebSocketMessageType.Text, true, token);
            return true;
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
