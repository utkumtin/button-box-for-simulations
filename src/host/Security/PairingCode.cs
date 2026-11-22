using System.Security.Cryptography;

namespace ButtonBox.Security;

public enum PairingCodeResult
{
    Success,
    Wrong,
    Expired,
    Burned,
    AlreadyUsed,
}

/// <summary>
/// A pairing code: 6 digits, 2 minutes, single use, 5 wrong attempts burn it for the whole pairing session (decisions §9).
/// Thread-safe.
/// </summary>
public sealed class PairingCode
{
    public const int Length = 6;
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time;
    private readonly byte[] _expected;
    private readonly object _gate = new();
    private int _attemptsLeft = MaxAttempts;
    private bool _used;

    private PairingCode(TimeProvider time)
    {
        _time = time;
        Code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        _expected = System.Text.Encoding.ASCII.GetBytes(Code);
        ExpiresAt = time.GetUtcNow() + Lifetime;
    }

    public string Code { get; }

    public DateTimeOffset ExpiresAt { get; }

    public int AttemptsLeft
    {
        get
        {
            lock (_gate)
            {
                return _attemptsLeft;
            }
        }
    }

    public static PairingCode Create(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return new PairingCode(time);
    }

    public PairingCodeResult TryUse(string? candidate)
    {
        lock (_gate)
        {
            if (_used)
            {
                return PairingCodeResult.AlreadyUsed;
            }

            if (_attemptsLeft == 0)
            {
                return PairingCodeResult.Burned;
            }

            if (_time.GetUtcNow() >= ExpiresAt)
            {
                return PairingCodeResult.Expired;
            }

            // Fixed-size buffer: the comparison always covers 6 bytes whatever the candidate length, so timing does not reveal it.
            Span<byte> buffer = stackalloc byte[Length];
            candidate ??= string.Empty;
            for (var i = 0; i < Length; i++)
            {
                var c = i < candidate.Length ? candidate[i] : '\0';
                buffer[i] = c < 128 ? (byte)c : (byte)0xFF;
            }

            var match = CryptographicOperations.FixedTimeEquals(buffer, _expected) & (candidate.Length == Length);
            if (match)
            {
                _used = true;
                return PairingCodeResult.Success;
            }

            _attemptsLeft--;
            return PairingCodeResult.Wrong;
        }
    }
}
