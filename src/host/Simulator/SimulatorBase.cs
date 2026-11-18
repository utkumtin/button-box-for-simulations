using ButtonBox.Core;

namespace ButtonBox.Simulator;

/// <summary>How an output reached the simulator.</summary>
public enum OutputKind
{
    Press,
    Hold,
    Release,
}

/// <summary>One output the host sent, recorded in arrival order.</summary>
public sealed record ReceivedOutput(OutputKind Kind, GameOutput Output);

/// <summary>
/// Shared plumbing for the game simulators: output record, scenario knobs, telemetry snapshots,
/// cancellable timers on an injected <see cref="TimeProvider"/>. Subclasses only model the game's rules.
/// </summary>
public abstract class SimulatorBase : IOutputBackend, IStateProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (ITimer Timer, long Generation)> _timers = [];
    private readonly List<ReceivedOutput> _received = [];
    private readonly Dictionary<string, int> _fields;
    private readonly TimeProvider _time;
    private bool _telemetryAvailable = true;
    private bool _gameListening = true;
    private long _generation;

    protected SimulatorBase(TimeProvider? timeProvider, IEnumerable<string> fieldNames, TimeSpan starterDelay)
    {
        _time = timeProvider ?? TimeProvider.System;
        _fields = fieldNames.ToDictionary(n => n, _ => 0);
        StarterDelay = starterDelay;
    }

    /// <inheritdoc />
    public event EventHandler<TelemetrySnapshot>? Changed;

    /// <summary>Time from starting the engine to "engine running". Read when the start begins.</summary>
    public TimeSpan StarterDelay { get; set; }

    /// <summary>Scenario: the engine never starts (for the host's amber timeout). Read when the start begins.</summary>
    public bool EngineNeverStarts { get; set; }

    /// <summary>Scenario: false means no telemetry (<see cref="TelemetrySnapshot.Available"/> false, no fields).</summary>
    public bool TelemetryAvailable
    {
        get { lock (_gate) { return _telemetryAvailable; } }
        set => Run(() => Change(ref _telemetryAvailable, value));
    }

    /// <summary>Scenario: false means paused/unfocused/not LIVE; the game ignores all outputs.</summary>
    public bool GameListening
    {
        get { lock (_gate) { return _gameListening; } }
        set => Run(() => Change(ref _gameListening, value));
    }

    /// <summary>Every output received so far, in order, including those the game ignored.</summary>
    public IReadOnlyList<ReceivedOutput> Received
    {
        get { lock (_gate) { return _received.ToArray(); } }
    }

    /// <inheritdoc />
    public TelemetrySnapshot Current
    {
        get { lock (_gate) { return BuildSnapshot(); } }
    }

    /// <inheritdoc />
    public Task PressAsync(GameOutput output, CancellationToken cancellationToken = default) =>
        Receive(OutputKind.Press, output, cancellationToken);

    /// <inheritdoc />
    public Task HoldAsync(GameOutput output, CancellationToken cancellationToken = default) =>
        Receive(OutputKind.Hold, output, cancellationToken);

    /// <inheritdoc />
    public Task ReleaseAsync(GameOutput output, CancellationToken cancellationToken = default) =>
        Receive(OutputKind.Release, output, cancellationToken);

    /// <summary>Applies one output to the game model. Called under the lock, only while the game listens.</summary>
    /// <returns>True when the telemetry changed.</returns>
    protected abstract bool Handle(OutputKind kind, GameOutput output);

    /// <summary>Gets a field value. Call only under the lock (inside <see cref="Handle"/> or a scheduled action).</summary>
    protected int Get(string field) => _fields[field];

    /// <summary>Sets a field value. Call only under the lock. Returns true when the value changed.</summary>
    protected bool Set(string field, int value)
    {
        if (_fields[field] == value)
        {
            return false;
        }

        _fields[field] = value;
        return true;
    }

    protected bool IsScheduled(string key) => _timers.ContainsKey(key);

    /// <summary>
    /// Runs <paramref name="action"/> under the lock after <paramref name="delay"/>, unless cancelled or replaced
    /// first. Call only under the lock. A cancelled timer can never fire its action, even if it was already queued.
    /// A zero or negative delay runs the action now.
    /// </summary>
    /// <returns>True when an immediate action changed the telemetry; always false for a delayed action.</returns>
    protected bool Schedule(string key, TimeSpan delay, Func<bool> action)
    {
        Cancel(key);
        if (delay <= TimeSpan.Zero)
        {
            return action();
        }

        var generation = ++_generation;
        var timer = _time.CreateTimer(
            _ => Run(() => Fire(key, generation, action)),
            null,
            delay,
            Timeout.InfiniteTimeSpan);
        _timers[key] = (timer, generation);
        return false;
    }

    /// <summary>Cancels a pending timer. Call only under the lock.</summary>
    protected void Cancel(string key)
    {
        if (_timers.Remove(key, out var entry))
        {
            entry.Timer.Dispose();
        }
    }

    private bool Fire(string key, long generation, Func<bool> action)
    {
        if (!_timers.TryGetValue(key, out var entry) || entry.Generation != generation)
        {
            return false;
        }

        _timers.Remove(key);
        entry.Timer.Dispose();
        return action();
    }

    private Task Receive(OutputKind kind, GameOutput output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Run(() =>
        {
            _received.Add(new ReceivedOutput(kind, output));
            return _gameListening && Handle(kind, output);
        });
        return Task.CompletedTask;
    }

    private static bool Change(ref bool field, bool value)
    {
        if (field == value)
        {
            return false;
        }

        field = value;
        return true;
    }

    /// <summary>Runs a state mutation under the lock; raises <see cref="Changed"/> after releasing it.</summary>
    private void Run(Func<bool> mutation)
    {
        TelemetrySnapshot? snapshot = null;
        lock (_gate)
        {
            if (mutation())
            {
                snapshot = BuildSnapshot();
            }
        }

        if (snapshot is not null)
        {
            Changed?.Invoke(this, snapshot);
        }
    }

    private TelemetrySnapshot BuildSnapshot() =>
        _telemetryAvailable
            ? new TelemetrySnapshot(true, _gameListening, new Dictionary<string, int>(_fields))
            : new TelemetrySnapshot(false, _gameListening, new Dictionary<string, int>());
}
