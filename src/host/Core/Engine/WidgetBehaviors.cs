using ButtonBox.Core.Contracts;

namespace ButtonBox.Core.Engine;

/// <summary>
/// What an accepted gesture does: the outputs, the commanded switch position, and a commit that runs only once the game is
/// known to be listening (behaviour state must not change for a dropped press). The commit returns true when the phone's view changed.
/// </summary>
internal sealed record GesturePlan(IReadOnlyList<GameOutput> Outputs, int? NewPosition, Func<DateTimeOffset, bool>? Commit = null)
{
    public static GesturePlan None { get; } = new([], null);
}

/// <summary>
/// The state one behaviour widget keeps between gestures and telemetry (decisions §5). Every member runs under the owning
/// <see cref="LayoutEngine"/>'s lock. Time is passed in; nothing here creates a timer.
/// </summary>
internal abstract class BehaviorState
{
    /// <summary>When <see cref="Observe"/> will change something without new telemetry, or null. Cleared once an Observe at that time has acted on it.</summary>
    public virtual DateTimeOffset? Deadline => null;

    /// <summary>Applies telemetry and the passage of time. <paramref name="position"/> is the commanded position of a switch (unused by others). True when the phone's view changed.</summary>
    public abstract bool Observe(TelemetrySnapshot snapshot, DateTimeOffset now, ref int position);

    public abstract void Reset();
}

/// <summary>Headlight rotary (decisions §5.1): positions 0 OFF, 1 AUTO, 2 LOW, 3 HIGH.</summary>
internal sealed class HeadlightsState : BehaviorState
{
    public static readonly TimeSpan PauseLength = TimeSpan.FromSeconds(1);

    private int? _pauseTarget;
    private DateTimeOffset _pauseEnd;

    public override DateTimeOffset? Deadline => _pauseTarget is null ? null : _pauseEnd;

    /// <summary>
    /// The light action of the target position, and <c>high-beam</c> (a toggle) only when telemetry shows the high-beam state
    /// differs from the target's. Without telemetry it is not sent.
    /// </summary>
    public static IReadOnlyList<GameOutput> Outputs(SwitchWidget widget, int target, TelemetrySnapshot snapshot, IReadOnlyDictionary<string, GameOutput> outputs)
    {
        var light = outputs[widget.Positions[target].Action!];
        if (snapshot.Available && snapshot.Fields.TryGetValue(ScsFields.HighBeam, out var high) && (high != 0) != (target == 3))
        {
            return [light, outputs[ScsActions.HighBeam]];
        }

        return [light];
    }

    /// <summary>The user turned the knob: inference waits until telemetry shows the target or the pause ends.</summary>
    public void Commanded(int target, DateTimeOffset now)
    {
        _pauseTarget = target;
        _pauseEnd = now + PauseLength;
    }

    /// <summary>
    /// The knob position telemetry shows, or null when it cannot be told (no telemetry or a field missing). A high-beam flag
    /// with the low beam off is ignored.
    /// </summary>
    public static int? Infer(TelemetrySnapshot snapshot)
    {
        if (!snapshot.Available
            || !snapshot.Fields.TryGetValue(ScsFields.Parking, out var parking)
            || !snapshot.Fields.TryGetValue(ScsFields.LowBeam, out var low)
            || !snapshot.Fields.TryGetValue(ScsFields.HighBeam, out var high))
        {
            return null;
        }

        if (parking == 0)
        {
            return 0;
        }

        if (low == 0)
        {
            return 1;
        }

        return high == 0 ? 2 : 3;
    }

    public override bool Observe(TelemetrySnapshot snapshot, DateTimeOffset now, ref int position)
    {
        var inferred = Infer(snapshot);
        if (_pauseTarget is { } target)
        {
            if (inferred != target && now < _pauseEnd)
            {
                return false;
            }

            _pauseTarget = null;
        }

        if (inferred is { } shown && shown != position)
        {
            position = shown;
            return true;
        }

        return false;
    }

    public override void Reset() => _pauseTarget = null;
}

/// <summary>
/// Wiper rotary (decisions §5.3): positions 0 OFF, 1 AUTO, 2 LOW, 3 FAST. The knob follows <c>truck.wipers</c> (on/off only) with
/// the same inference pause as the headlights.
/// </summary>
internal sealed class WipersState : BehaviorState
{
    public static readonly TimeSpan PauseLength = TimeSpan.FromSeconds(1);

    private int? _pauseTarget;
    private DateTimeOffset _pauseEnd;

    public override DateTimeOffset? Deadline => _pauseTarget is null ? null : _pauseEnd;

    /// <summary>The user turned the knob: inference waits until telemetry shows the target or the pause ends.</summary>
    public void Commanded(int target, DateTimeOffset now)
    {
        _pauseTarget = target;
        _pauseEnd = now + PauseLength;
    }

    /// <summary>
    /// The knob position telemetry shows, given the knob's <paramref name="position"/>, or null without the field. Off is OFF.
    /// On keeps AUTO, LOW or FAST (the flag cannot tell them apart) and turns OFF into AUTO, the game's first wiper stage.
    /// </summary>
    public static int? Infer(TelemetrySnapshot snapshot, int position)
    {
        if (!snapshot.Available || !snapshot.Fields.TryGetValue(ScsFields.Wipers, out var on))
        {
            return null;
        }

        if (on == 0)
        {
            return 0;
        }

        return position == 0 ? 1 : position;
    }

    public override bool Observe(TelemetrySnapshot snapshot, DateTimeOffset now, ref int position)
    {
        var inferred = Infer(snapshot, position);
        if (_pauseTarget is { } target)
        {
            if (inferred != target && now < _pauseEnd)
            {
                return false;
            }

            _pauseTarget = null;
        }

        if (inferred is { } shown && shown != position)
        {
            position = shown;
            return true;
        }

        return false;
    }

    public override void Reset() => _pauseTarget = null;
}

/// <summary>Engine start-stop button (decisions §5.2). Its lit, LED and amber timer come from here, not from a state binding.</summary>
internal sealed class EngineStartStopState : BehaviorState
{
    public static readonly TimeSpan AmberTimeout = TimeSpan.FromSeconds(4);

    private bool _amber;
    private DateTimeOffset? _deadline;
    private bool? _electricWasOn; // null: not seen yet or no telemetry

    public override DateTimeOffset? Deadline => _deadline;

    /// <summary>
    /// The tap/hold matrix. Without telemetry the truck counts as all off, so a gesture can start the engine but never stop it.
    /// A tap with the engine running has no outputs.
    /// </summary>
    public GesturePlan Plan(Gesture gesture, DualActionWidget widget, TelemetrySnapshot snapshot, IReadOnlyDictionary<string, GameOutput> outputs)
    {
        var electric = ScsFields.ValueOf(snapshot, ScsFields.Electric) != 0;
        var running = ScsFields.ValueOf(snapshot, ScsFields.EngineRunning) != 0;
        var ignitionOn = outputs[widget.Tap!];
        var starter = outputs[widget.Hold!];
        var ignitionOff = outputs[ScsActions.IgnitionOff];

        if (gesture == Gesture.Tap)
        {
            if (!electric)
            {
                return new GesturePlan([ignitionOn], null);
            }

            return running ? GesturePlan.None : new GesturePlan([ignitionOff], null, _ => ClearAll());
        }

        if (running)
        {
            return new GesturePlan([ignitionOff], null, _ => ClearAll());
        }

        return new GesturePlan(electric ? [starter] : [ignitionOn, starter], null, StartAttempt);
    }

    // The gesture that turns electric off clears every light.
    private bool ClearAll()
    {
        var changed = _amber;
        _amber = false;
        _deadline = null;
        return changed;
    }

    // A hold that sends the starter is a new attempt: amber clears now and the 4 s timer starts at acceptance.
    private bool StartAttempt(DateTimeOffset now)
    {
        var changed = _amber;
        _amber = false;
        _deadline = now + AmberTimeout;
        return changed;
    }

    public override bool Observe(TelemetrySnapshot snapshot, DateTimeOffset now, ref int position)
    {
        // Electric turned off outside the app (keyboard) clears every light, like the electric-off gesture does. Only the on→off
        // change counts: right after a hold from all off, electric is still 0 for a moment, and that must not cancel the attempt.
        var wasOn = _electricWasOn;
        _electricWasOn = snapshot.Available ? ScsFields.ValueOf(snapshot, ScsFields.Electric) != 0 : null;
        if (wasOn == true && _electricWasOn == false)
        {
            return ClearAll();
        }

        if (ScsFields.ValueOf(snapshot, ScsFields.EngineRunning) != 0)
        {
            // A running engine supersedes a failed attempt and ends a pending one.
            var changed = _amber;
            _amber = false;
            _deadline = null;
            return changed;
        }

        if (_deadline is { } deadline && now >= deadline)
        {
            _deadline = null;
            _amber = true;
            return true;
        }

        return false;
    }

    /// <summary>Lit follows the electric flag; the LED is green while the engine runs, amber after a failed attempt, otherwise off.</summary>
    public WidgetState Project(TelemetrySnapshot snapshot) => new()
    {
        Lit = ScsFields.ValueOf(snapshot, ScsFields.Electric) != 0,
        Led = ScsFields.ValueOf(snapshot, ScsFields.EngineRunning) != 0 ? Led.Green : _amber ? Led.Amber : Led.Off,
    };

    public override void Reset()
    {
        _amber = false;
        _deadline = null;
        _electricWasOn = null;
    }
}
