using System.Net.NetworkInformation;

namespace ButtonBox.Discovery;

/// <summary>Raises a signal whenever the OS thinks the network may have changed. Injectable so tests need no real network events.</summary>
public interface INetworkChangeSource
{
    event Action? Signaled;
}

/// <summary>Maps <see cref="NetworkChange"/> events to <see cref="INetworkChangeSource"/>.</summary>
public sealed class SystemNetworkChangeSource : INetworkChangeSource, IDisposable
{
    public SystemNetworkChangeSource()
    {
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    public event Action? Signaled;

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
    }

    private void OnAddressChanged(object? sender, EventArgs e) => Signaled?.Invoke();

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Signaled?.Invoke();
}

/// <summary>
/// Watches the LAN address set. OS events arrive in bursts (one reconnect produces many), so they are debounced and
/// <see cref="Changed"/> fires only if the selected set really differs from the last one reported.
/// </summary>
public sealed class NetworkWatcher : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    private readonly Func<IReadOnlyList<LanAddress>> _source;
    private readonly INetworkChangeSource _signals;
    private readonly ITimer _timer;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private IReadOnlyList<LanAddress> _current;
    private bool _disposed;

    public NetworkWatcher()
        : this(LanInterfaces.Current, new SystemNetworkChangeSource(), TimeProvider.System, DefaultDebounce)
    {
    }

    /// <summary>The watcher disposes <paramref name="signals"/> if it is <see cref="IDisposable"/>.</summary>
    public NetworkWatcher(Func<IReadOnlyList<LanAddress>> source, INetworkChangeSource signals, TimeProvider timeProvider, TimeSpan debounce)
    {
        _source = source;
        _signals = signals;
        _debounce = debounce;
        _current = source();
        _timer = timeProvider.CreateTimer(_ => Evaluate(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _signals.Signaled += OnSignaled;
    }

    /// <summary>Raised on a timer thread with the new set.</summary>
    public event Action<IReadOnlyList<LanAddress>>? Changed;

    public IReadOnlyList<LanAddress> Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _signals.Signaled -= OnSignaled;
        _timer.Dispose();
        (_signals as IDisposable)?.Dispose();
    }

    private void OnSignaled()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan); // each signal restarts the quiet period
            }
        }
    }

    private void Evaluate()
    {
        IReadOnlyList<LanAddress> next;
        try
        {
            next = _source();
        }
        catch (NetworkInformationException)
        {
            return; // keep the last known set; the next OS event retries
        }

        lock (_gate)
        {
            if (_disposed || _current.SequenceEqual(next))
            {
                return;
            }

            _current = next;
        }

        Changed?.Invoke(next);
    }
}
