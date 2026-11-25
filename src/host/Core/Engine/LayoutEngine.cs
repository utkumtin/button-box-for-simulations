using ButtonBox.Core.Contracts;

namespace ButtonBox.Core.Engine;

/// <summary>
/// Gesture mapping and state projection for one layout. Thread-safe: gestures arrive from several sockets.
/// The phone only names a widget and a gesture; every output comes from the layout and catalog.
/// </summary>
public sealed class LayoutEngine
{
    private readonly Layout _layout;
    private readonly Dictionary<string, Widget> _widgets = new();
    private readonly IReadOnlyDictionary<string, GameOutput> _outputs;
    private readonly Dictionary<string, int> _positions = new();
    private readonly Dictionary<string, BehaviorState> _behaviors = new();
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    // The profile has passed ProfileCheck: widget ids are unique and every referenced action exists.
    internal LayoutEngine(Layout layout, IReadOnlyDictionary<string, GameOutput> outputs, TimeProvider time)
    {
        _layout = layout;
        _outputs = outputs;
        _time = time;
        foreach (var widget in layout.Widgets)
        {
            _widgets.Add(widget.Id, widget);
            if (widget is SwitchWidget)
            {
                _positions[widget.Id] = 0;
            }

            BehaviorState? behavior = widget switch
            {
                SwitchWidget { Behavior: SwitchBehavior.Headlights } => new HeadlightsState(),
                SwitchWidget { Behavior: SwitchBehavior.Wipers } => new WipersState(),
                DualActionWidget { Behavior: DualActionBehavior.EngineStartStop } => new EngineStartStopState(),
                _ => null,
            };
            if (behavior is not null)
            {
                _behaviors.Add(widget.Id, behavior);
            }
        }
    }

    public Layout Layout => _layout;

    internal void ResetPositions()
    {
        lock (_gate)
        {
            foreach (var id in _positions.Keys.ToList())
            {
                _positions[id] = 0;
            }

            foreach (var behavior in _behaviors.Values)
            {
                behavior.Reset();
            }
        }
    }

    /// <summary>
    /// Takes over the commanded positions of <paramref name="previous"/> (the layout this one replaces): a switch keeps its
    /// position when its widget id exists in both and the number of positions is the same; every other switch starts at 0.
    /// Call before this engine is published.
    /// </summary>
    internal void CarryPositionsFrom(LayoutEngine previous)
    {
        Dictionary<string, (int Count, int Position)> old = new();
        lock (previous._gate)
        {
            foreach (var (id, position) in previous._positions)
            {
                old[id] = (((SwitchWidget)previous._widgets[id]).Positions.Count, position);
            }
        }

        lock (_gate)
        {
            foreach (var id in _positions.Keys.ToList())
            {
                var count = ((SwitchWidget)_widgets[id]).Positions.Count;
                _positions[id] = old.TryGetValue(id, out var was) && was.Count == count ? was.Position : 0;
            }
        }
    }

    /// <summary>Validates a client message, maps it to outputs and updates commanded switch positions.</summary>
    public HandleResult Handle(ClientMessage message, TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(snapshot);

        string? widgetId = null;
        Gesture? gesture = null;
        int? select = null;
        switch (message)
        {
            case GestureMessage g:
                widgetId = g.WidgetId;
                gesture = g.Gesture;
                break;
            case SelectMessage s:
                widgetId = s.WidgetId;
                select = s.Position;
                break;
        }
        if (widgetId is null || !_widgets.TryGetValue(widgetId, out var widget))
        {
            return HandleResult.Invalid;
        }

        lock (_gate)
        {
            // Validity first, then listening, then commit any position change.
            var plan = Resolve(widget, gesture, select, snapshot);
            if (plan is null)
            {
                return HandleResult.Invalid;
            }
            if (!snapshot.GameListening)
            {
                return HandleResult.NotListening;
            }

            var changed = false;
            if (plan.NewPosition is { } position)
            {
                changed = _positions[widget.Id] != position;
                _positions[widget.Id] = position;
            }
            if (plan.Commit is { } commit)
            {
                changed |= commit(_time.GetUtcNow());
            }
            return new HandleResult(HandleOutcome.Accepted, plan.Outputs, changed);
        }
    }

    /// <summary>
    /// Applies what changed without a gesture, at the clock's current time: the headlight and wiper inference and the end of their
    /// pauses, the amber timeout, a running engine clearing amber. CPU only. True when anything the phone shows changed.
    /// </summary>
    public bool Observe(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            if (_behaviors.Count == 0)
            {
                return false;
            }

            var now = _time.GetUtcNow();
            var changed = false;
            foreach (var (id, behavior) in _behaviors)
            {
                var hasPosition = _positions.TryGetValue(id, out var position);
                if (behavior.Observe(snapshot, now, ref position))
                {
                    if (hasPosition)
                    {
                        _positions[id] = position;
                    }
                    changed = true;
                }
            }
            return changed;
        }
    }

    /// <summary>The earliest time at which <see cref="Observe"/> changes something without new telemetry (an amber deadline, the end of an inference pause), or null.</summary>
    public DateTimeOffset? NextDeadline
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset? next = null;
                foreach (var behavior in _behaviors.Values)
                {
                    if (behavior.Deadline is { } deadline && (next is null || deadline < next))
                    {
                        next = deadline;
                    }
                }
                return next;
            }
        }
    }

    /// <summary>Projects telemetry and commanded switch positions into the state message for the phone.</summary>
    public StateMessage Project(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var states = new Dictionary<string, WidgetState>();
        lock (_gate)
        {
            foreach (var widget in _layout.Widgets)
            {
                var state = new WidgetState();
                var any = false;

                if (snapshot.Available && widget.State is { } binding)
                {
                    if (binding.Lit is { } litField && snapshot.Fields.TryGetValue(litField, out var lit))
                    {
                        state.Lit = lit != 0;
                        any = true;
                    }
                    if (binding.Led is { } ledField && snapshot.Fields.TryGetValue(ledField, out var led))
                    {
                        state.Led = led != 0 ? Led.Green : Led.Off;
                        any = true;
                    }
                }

                if (widget is SwitchWidget)
                {
                    state.Position = _positions[widget.Id];
                    any = true;
                }

                if (snapshot.Available && _behaviors.TryGetValue(widget.Id, out var behavior) && behavior is EngineStartStopState engine)
                {
                    var shown = engine.Project(snapshot);
                    state.Lit = shown.Lit;
                    state.Led = shown.Led;
                    any = true;
                }

                if (any)
                {
                    states.Add(widget.Id, state);
                }
            }
        }
        return new StateMessage { Widgets = states };
    }

    public StatusMessage Status(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new StatusMessage { Telemetry = snapshot.Available, GameListening = snapshot.GameListening };
    }

    // Must be called under _gate (reads commanded positions). Null means invalid.
    private GesturePlan? Resolve(Widget widget, Gesture? gesture, int? select, TelemetrySnapshot snapshot)
    {
        GesturePlan One(string actionId, int? position = null) => new([_outputs[actionId]], position);

        switch (widget)
        {
            case ToggleWidget toggle when gesture == Gesture.Tap:
                return One(toggle.Action);

            case EncoderWidget encoder when gesture == Gesture.Minus:
                return One(encoder.Minus);
            case EncoderWidget encoder when gesture == Gesture.Plus:
                return One(encoder.Plus);

            case SwitchWidget sw:
                {
                    var count = sw.Positions.Count;
                    if (count == 0)
                    {
                        return null;
                    }

                    int target;
                    if (select is { } n)
                    {
                        if (n < 0 || n >= count)
                        {
                            return null;
                        }
                        target = n; // sent even when equal to the current position
                    }
                    else if (gesture is Gesture.Previous or Gesture.Next)
                    {
                        var current = _positions[sw.Id];
                        target = Math.Clamp(current + (gesture == Gesture.Next ? 1 : -1), 0, count - 1);
                        if (target == current)
                        {
                            return GesturePlan.None;
                        }
                    }
                    else
                    {
                        return null;
                    }
                    if (_behaviors.TryGetValue(sw.Id, out var behavior) && behavior is HeadlightsState headlights)
                    {
                        var outputs = HeadlightsState.Outputs(sw, target, snapshot, _outputs);
                        return new GesturePlan(outputs, target, now => { headlights.Commanded(target, now); return false; });
                    }
                    if (behavior is WipersState wipers)
                    {
                        return new GesturePlan([_outputs[sw.Positions[target].Action!]], target, now => { wipers.Commanded(target, now); return false; });
                    }
                    return One(sw.Positions[target].Action!, target);
                }

            case DualActionWidget { Behavior: DualActionBehavior.EngineStartStop } dual
                when gesture is Gesture.Tap or Gesture.Hold:
                return ((EngineStartStopState)_behaviors[dual.Id]).Plan(gesture.Value, dual, snapshot, _outputs);

            case DualActionWidget dual when gesture == Gesture.Tap && dual.Tap is not null:
                return One(dual.Tap);
            case DualActionWidget dual when gesture == Gesture.Hold && dual.Hold is not null
                                            && dual.HoldMode == DualActionWidgetHoldMode.Fire:
                return One(dual.Hold);

            default:
                return null;
        }
    }
}
