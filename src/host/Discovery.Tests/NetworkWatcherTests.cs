using System.Net;
using Xunit;

namespace ButtonBox.Discovery.Tests;

/// <summary>A TimeProvider whose timers only fire when the test advances time.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        foreach (var t in _timers.ToArray())
        {
            t.FireIfDue(_now);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void FireIfDue(DateTimeOffset now)
        {
            if (_due is { } d && d <= now)
            {
                _due = null;
                callback(state);
            }
        }

        public void Dispose()
        {
            _due = null;
            owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

// OS network events come in bursts and often do not change the LAN set at all. Without debouncing and the "really
// different" check, the HTTPS server and the mDNS responder would restart (and re-announce) on every blip.
public sealed class NetworkWatcherTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly LanAddress Eth = new(7, IPAddress.Parse("192.168.1.20"), 24);
    private static readonly LanAddress Wifi = new(12, IPAddress.Parse("10.0.0.5"), 24);

    private sealed class FakeSignals : INetworkChangeSource
    {
        public event Action? Signaled;

        public void Raise() => Signaled?.Invoke();
    }

    private sealed class Harness : IDisposable
    {
        public Harness(params LanAddress[] initial)
        {
            Set = initial;
            Watcher = new NetworkWatcher(() => Set, Signals, Time, Debounce);
            Watcher.Changed += set => Raised.Add(set);
        }

        public ManualTimeProvider Time { get; } = new();

        public FakeSignals Signals { get; } = new();

        public NetworkWatcher Watcher { get; }

        public LanAddress[] Set { get; set; }

        public List<IReadOnlyList<LanAddress>> Raised { get; } = [];

        public void Dispose() => Watcher.Dispose();
    }

    [Fact]
    public void A_burst_of_events_collapses_into_one_change_after_the_quiet_period()
    {
        using var h = new Harness(Eth);
        h.Set = [Eth, Wifi];

        for (var i = 0; i < 10; i++)
        {
            h.Signals.Raise();
            h.Time.Advance(TimeSpan.FromMilliseconds(500)); // each event arrives before the 2 s are up
        }

        Assert.Empty(h.Raised); // still within the quiet period of the last event
        h.Time.Advance(Debounce);

        var raised = Assert.Single(h.Raised);
        Assert.Equal([Eth, Wifi], raised);
        Assert.Equal([Eth, Wifi], h.Watcher.Current);
    }

    [Fact]
    public void Nothing_is_raised_before_the_debounce_has_elapsed()
    {
        using var h = new Harness(Eth);
        h.Set = [Wifi];

        h.Signals.Raise();
        h.Time.Advance(Debounce - TimeSpan.FromMilliseconds(1));

        Assert.Empty(h.Raised);
    }

    [Fact]
    public void Events_that_leave_the_set_unchanged_raise_nothing()
    {
        using var h = new Harness(Eth, Wifi);

        h.Signals.Raise();
        h.Time.Advance(Debounce);
        h.Signals.Raise();
        h.Time.Advance(Debounce);

        Assert.Empty(h.Raised);
    }

    // Reporting must be relative to the last reported set: flapping back to the old set is a change, and the same new
    // set seen twice is not.
    [Fact]
    public void Each_real_difference_is_reported_once()
    {
        using var h = new Harness(Eth);

        h.Set = [Wifi];
        h.Signals.Raise();
        h.Time.Advance(Debounce);
        h.Signals.Raise();
        h.Time.Advance(Debounce);
        h.Set = [Eth];
        h.Signals.Raise();
        h.Time.Advance(Debounce);

        Assert.Equal(2, h.Raised.Count);
        Assert.Equal([Wifi], h.Raised[0]);
        Assert.Equal([Eth], h.Raised[1]);
    }

    [Fact]
    public void Losing_all_LAN_interfaces_is_reported_as_an_empty_set()
    {
        using var h = new Harness(Eth);
        h.Set = [];

        h.Signals.Raise();
        h.Time.Advance(Debounce);

        Assert.Empty(Assert.Single(h.Raised));
    }

    [Fact]
    public void After_dispose_events_are_ignored()
    {
        var h = new Harness(Eth);
        h.Set = [Wifi];
        h.Watcher.Dispose();

        h.Signals.Raise();
        h.Time.Advance(Debounce);

        Assert.Empty(h.Raised);
    }
}
