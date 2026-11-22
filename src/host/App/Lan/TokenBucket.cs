namespace ButtonBox.App.Lan;

/// <summary>
/// Token bucket driven by an injected <see cref="TimeProvider"/>. Refill is computed from elapsed time on each call, so
/// there is no background timer and a test with a fake clock sees an exact, repeatable burst limit.
/// </summary>
public sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly double _capacity;
    private readonly double _perSecond;
    private readonly TimeProvider _time;
    private double _tokens;
    private long _last;

    public TokenBucket(int capacity, double refillPerSecond, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refillPerSecond);
        ArgumentNullException.ThrowIfNull(time);
        _capacity = capacity;
        _perSecond = refillPerSecond;
        _time = time;
        _tokens = capacity;
        _last = time.GetTimestamp();
    }

    /// <summary>Takes one token. False when the bucket is empty.</summary>
    public bool TryTake()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var elapsed = _time.GetElapsedTime(_last, now);
            _last = now;
            _tokens = Math.Min(_capacity, _tokens + elapsed.TotalSeconds * _perSecond);
            if (_tokens < 1)
            {
                return false;
            }

            _tokens -= 1;
            return true;
        }
    }
}
