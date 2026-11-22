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
    private readonly object _gate = new();

    // The profile has passed ProfileCheck: widget ids are unique and every referenced action exists.
    internal LayoutEngine(Layout layout, IReadOnlyDictionary<string, GameOutput> outputs)
    {
        _layout = layout;
        _outputs = outputs;
        foreach (var widget in layout.Widgets)
        {
            _widgets.Add(widget.Id, widget);
            if (widget is SwitchWidget)
            {
                _positions[widget.Id] = 0;
            }
        }
    }

    public Layout Layout => _layout;

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
            var plan = Resolve(widget, gesture, select);
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
            return new HandleResult(HandleOutcome.Accepted, plan.Outputs, changed);
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

    private sealed record Step(IReadOnlyList<GameOutput> Outputs, int? NewPosition);

    private static readonly Step NoOp = new([], null);

    // Must be called under _gate (reads commanded positions). Null means invalid.
    private Step? Resolve(Widget widget, Gesture? gesture, int? select)
    {
        Step One(string actionId, int? position = null) => new([_outputs[actionId]], position);

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
                            return NoOp;
                        }
                    }
                    else
                    {
                        return null;
                    }
                    return One(sw.Positions[target].Action!, target);
                }

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
