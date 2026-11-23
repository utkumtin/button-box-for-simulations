using System.Collections;
using System.Runtime.InteropServices;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;

namespace ButtonBox.App;

/// <summary>The Windows Firewall rule's state for the Settings page (decisions §11). Read-only; never changes or elevates anything.</summary>
public interface IFirewallStatus
{
    Task<FirewallState> GetAsync();
}

/// <summary>
/// <c>present</c> when an enabled inbound rule of our group exists, <c>missing</c> when none, <c>unknown</c> on any failure or timeout.
/// The probe runs off the caller's thread with a timeout, one at a time (a stuck probe is not stacked up), and the answer is cached.
/// </summary>
public sealed class FirewallStatus : IFirewallStatus
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    private const int InboundDirection = 1; // NET_FW_RULE_DIR_IN

    private readonly Func<FirewallState> _probe;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Task<FirewallState>? _inflight;
    private FirewallState _cached;
    private DateTimeOffset _validUntil = DateTimeOffset.MinValue;

    public FirewallStatus()
        : this(ReadWindowsFirewall, TimeProvider.System)
    {
    }

    /// <param name="probe">Blocking; may throw. Runs on a pool thread.</param>
    public FirewallStatus(Func<FirewallState> probe, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(time);
        _probe = probe;
        _time = time;
    }

    public async Task<FirewallState> GetAsync()
    {
        Task<FirewallState> task;
        lock (_gate)
        {
            if (_time.GetUtcNow() < _validUntil)
            {
                return _cached;
            }

            _inflight ??= Task.Run(RunProbe).ContinueWith(
                t =>
                {
                    lock (_gate)
                    {
                        _inflight = null;
                    }

                    return t.Result;
                },
                TaskScheduler.Default);
            task = _inflight;
        }

        FirewallState state;
        try
        {
            state = await task.WaitAsync(ProbeTimeout, _time);
        }
        catch (TimeoutException)
        {
            state = FirewallState.Unknown;
        }

        lock (_gate)
        {
            _cached = state;
            _validUntil = _time.GetUtcNow() + CacheFor;
        }

        return state;
    }

    private FirewallState RunProbe()
    {
        try
        {
            return _probe();
        }
        catch (Exception)
        {
            return FirewallState.Unknown; // COM not registered, access refused, anything: never an error to the editor
        }
    }

    private static FirewallState ReadWindowsFirewall()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
        if (type is null)
        {
            return FirewallState.Unknown;
        }

        var policy = Activator.CreateInstance(type);
        if (policy is null)
        {
            return FirewallState.Unknown;
        }

        try
        {
            dynamic rules = ((dynamic)policy).Rules;
            foreach (dynamic rule in (IEnumerable)rules)
            {
                try
                {
                    if (string.Equals((string?)rule.Grouping, FirewallRules.Group, StringComparison.OrdinalIgnoreCase)
                        && (bool)rule.Enabled
                        && (int)rule.Direction == InboundDirection)
                    {
                        return FirewallState.Present;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject((object)rule);
                }
            }

            return FirewallState.Missing;
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }
}
