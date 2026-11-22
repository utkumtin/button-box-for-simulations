using ButtonBox.Core;

namespace ButtonBox.App;

/// <summary>
/// One running app per Windows session (decisions §12). A second launch asks the first to open its editor and exits. This must
/// be the first thing <c>Main</c> does: the server key has one fixed name, so a second process that issued a certificate would
/// overwrite the running app's key.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _mutexName;
    private readonly EventWaitHandle _signal;
    private readonly ManualResetEventSlim _release = new(false);
    private Thread? _owner;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    /// <summary>The names are parameters so tests never touch the product's. Use the <c>Local\</c> prefix: one instance per session.</summary>
    public SingleInstance(string mutexName, string eventName)
    {
        ArgumentException.ThrowIfNullOrEmpty(mutexName);
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        _mutexName = mutexName;
        // Created (or opened) before the mutex is tried, so a second launch can never signal an event that does not exist yet.
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
    }

    public static SingleInstance ForProduct() =>
        new($@"Local\{ProductInfo.Name}.instance", $@"Local\{ProductInfo.Name}.open-editor");

    /// <summary>
    /// True when this is the first instance. A mutex belongs to the thread that took it, so a dedicated thread owns it until
    /// <see cref="Dispose"/>; that keeps it independent of the UI thread and of async continuations.
    /// </summary>
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_owner is not null)
        {
            throw new InvalidOperationException("Already acquired or tried.");
        }

        using var ready = new ManualResetEventSlim(false);
        var acquired = false;
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, _mutexName);
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // the previous owner died without releasing; we own it now
            }

            ready.Set();
            if (acquired)
            {
                _release.Wait();
                mutex.ReleaseMutex();
            }
        })
        { IsBackground = true, Name = "single-instance-owner" };
        thread.Start();
        ready.Wait();
        if (acquired)
        {
            _owner = thread;
        }
        else
        {
            thread.Join();
            _owner = thread; // remember that we tried
        }

        return acquired;
    }

    /// <summary>Asks the running instance to open its editor.</summary>
    public void SignalRunning() => _signal.Set();

    /// <summary>Runs <paramref name="onSignal"/> (on a pool thread) every time another launch signals. Only the first instance calls this.</summary>
    public void StartListening(Action onSignal)
    {
        ArgumentNullException.ThrowIfNull(onSignal);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_wait is not null)
        {
            throw new InvalidOperationException("Already listening.");
        }

        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, static (state, _) =>
        {
            try
            {
                ((Action)state!)();
            }
            catch (Exception)
            {
                // A failing handler must not end the listener.
            }
        }, onSignal, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wait?.Unregister(null);
        _release.Set();
        _owner?.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
        _release.Dispose();
    }
}
