using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using Xunit;
using G = ButtonBox.Core.Contracts.Gesture;
using static ButtonBox.Core.Tests.Engine.BehaviorFixture;
using static ButtonBox.Core.Tests.Engine.EngineFixture;

namespace ButtonBox.Core.Tests.Engine;

// Decisions §5.1: the headlight rotary sends a light action and, only when telemetry shows the high beam differs from the target,
// the high-beam toggle. The knob shows the commanded position until telemetry has caught up, then follows the keyboard.
public class HeadlightsBehaviorTests
{
    private readonly ManualTime _time = new();
    private readonly LayoutEngine _engine;

    public HeadlightsBehaviorTests() => _engine = NewEngine(_time, LightsWidget());

    public static TheoryData<int, int> EveryStartAndTarget()
    {
        var data = new TheoryData<int, int>();
        for (var start = 0; start < 4; start++)
        {
            for (var target = 0; target < 4; target++)
            {
                data.Add(start, target);
            }
        }

        return data;
    }

    private static string LightAction(int target) => target switch { 0 => "in-light-off", 1 => "in-light-park", _ => "in-light-on" };

    // The telemetry that matches a target's own high-beam state, so no high-beam toggle is due.
    private static TelemetrySnapshot Settled(int target) => LightsTele(target == 0 ? 0 : 1, target >= 2 ? 1 : 0, target == 3 ? 1 : 0);

    [Theory]
    [MemberData(nameof(EveryStartAndTarget))]
    public void Every_target_from_every_start_sends_its_light_action_and_commands_the_position(int start, int target)
    {
        // Why: the layout lists lightoff/lightpark/lighton twice; a wrong mapping would put the truck in another light state.
        _engine.Handle(Select(LightsId, start), Settled(start));

        var result = _engine.Handle(Select(LightsId, target), Settled(target));

        Assert.Equal(HandleOutcome.Accepted, result.Outcome);
        Assert.Equal([LightAction(target)], Names(result));
        Assert.Equal(target, PositionOf(_engine, LightsId));
    }

    [Theory]
    [InlineData(3, 0, new[] { "in-light-on", "in-high-beam" })] // HIGH with the high beam off: the toggle must follow
    [InlineData(3, 1, new[] { "in-light-on" })] // HIGH with the high beam already on: no toggle
    [InlineData(2, 1, new[] { "in-light-on", "in-high-beam" })] // LOW with a keyboard high beam: switch it off
    [InlineData(1, 1, new[] { "in-light-park", "in-high-beam" })]
    [InlineData(0, 0, new[] { "in-light-off" })] // OFF with the high beam off: lightoff only
    [InlineData(0, 1, new[] { "in-light-off", "in-high-beam" })]
    public void High_beam_toggle_is_sent_only_when_telemetry_differs_from_the_target(int target, int highBeam, string[] expected)
    {
        // Why: hblight is a toggle. Sending it blindly would turn the high beam off when it should be on.
        var result = _engine.Handle(Select(LightsId, target), LightsTele(1, 1, highBeam));

        Assert.Equal(expected, Names(result));
    }

    [Fact]
    public void Without_telemetry_the_high_beam_toggle_is_never_sent()
    {
        // Why: with no state to compare, a toggle is a coin flip, so HIGH sends only the light action.
        var unavailable = Snapshot(false, true);

        Assert.Equal(["in-light-on"], Names(_engine.Handle(Select(LightsId, 3), unavailable)));
        Assert.Equal(["in-light-off"], Names(_engine.Handle(Select(LightsId, 0), unavailable)));
    }

    [Fact]
    public void Previous_and_next_clamp_at_the_ends_and_send_the_toggle_when_reaching_high()
    {
        var atStart = _engine.Handle(Msg(LightsId, G.Previous), Settled(0));
        Assert.Empty(atStart.Outputs);
        Assert.False(atStart.StateChanged);

        _engine.Handle(Select(LightsId, 2), Settled(2));
        var toHigh = _engine.Handle(Msg(LightsId, G.Next), Settled(2));
        Assert.Equal(["in-light-on", "in-high-beam"], Names(toHigh));
        Assert.Equal(3, PositionOf(_engine, LightsId));

        var atEnd = _engine.Handle(Msg(LightsId, G.Next), Settled(3));
        Assert.Empty(atEnd.Outputs);
        Assert.Equal(3, PositionOf(_engine, LightsId));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)] // parking off: OFF
    [InlineData(0, 1, 1, 0)] // parking off beats everything
    [InlineData(1, 0, 0, 1)] // parking on, low off: AUTO
    [InlineData(1, 0, 1, 1)] // a high-beam flag with the low beam off is ignored
    [InlineData(1, 1, 0, 2)] // low on, high off: LOW
    [InlineData(1, 1, 1, 3)] // low on, high on: HIGH
    public void Telemetry_inference_table(int parking, int low, int high, int expected)
    {
        // Why: when the user changes lights from the keyboard the knob has to follow, by exactly this table.
        var from = expected == 3 ? LightsTele(1, 0, 0) : LightsTele(1, 1, 1);
        Assert.True(_engine.Observe(from));
        Assert.NotEqual(expected, PositionOf(_engine, LightsId));

        Assert.True(_engine.Observe(LightsTele(parking, low, high)));

        Assert.Equal(expected, PositionOf(_engine, LightsId));
    }

    [Fact]
    public void Inference_needs_telemetry_with_all_three_light_fields()
    {
        // Why: without data the knob keeps the commanded position instead of snapping to a guess.
        _engine.Handle(Select(LightsId, 2), Settled(2));
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(_engine.Observe(Snapshot(false, true)));
        Assert.False(_engine.Observe(Tele(("truck.light.parking", 0))));
        Assert.Equal(2, PositionOf(_engine, LightsId));
    }

    [Fact]
    public void High_from_off_never_shows_low_while_telemetry_passes_through_low_on_high_off()
    {
        // Why: lighton lands before hblight, so telemetry briefly says LOW. Without the pause the knob would flicker back to LOW.
        _engine.Handle(Select(LightsId, 3), LightsTele(0, 0, 0));

        Assert.False(_engine.Observe(LightsTele(1, 1, 0)));
        Assert.Equal(3, PositionOf(_engine, LightsId));

        Assert.False(_engine.Observe(LightsTele(1, 1, 1))); // matches the target: the pause ends, the knob is already there
        Assert.Equal(3, PositionOf(_engine, LightsId));
        Assert.Null(_engine.NextDeadline);
    }

    [Fact]
    public void After_the_pause_ended_by_a_match_a_keyboard_change_moves_the_knob_in_the_same_observe()
    {
        // Why: "changes from the keyboard move the knob within a frame": the pause is not a permanent freeze.
        _engine.Handle(Select(LightsId, 3), LightsTele(0, 0, 0));
        _engine.Observe(LightsTele(1, 1, 1));

        Assert.True(_engine.Observe(LightsTele(1, 1, 0)));

        Assert.Equal(2, PositionOf(_engine, LightsId));
    }

    [Fact]
    public void A_pause_that_telemetry_never_satisfies_ends_after_one_second_and_inference_runs_again()
    {
        // Why: if the game ignores the command, the knob must not stay wrong forever.
        _engine.Handle(Select(LightsId, 3), LightsTele(0, 0, 0));
        var started = _time.GetUtcNow();
        Assert.Equal(started + TimeSpan.FromSeconds(1), _engine.NextDeadline);

        _engine.Observe(LightsTele(1, 1, 0));
        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(_engine.Observe(LightsTele(1, 1, 0)));
        Assert.Equal(3, PositionOf(_engine, LightsId));

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(_engine.Observe(LightsTele(1, 1, 0)));
        Assert.Equal(2, PositionOf(_engine, LightsId));
        Assert.Null(_engine.NextDeadline);
    }

    [Fact]
    public void A_pause_without_telemetry_ends_after_one_second_without_a_change()
    {
        // Why: NextDeadline must not stay set (the session timer would fire again and again), and nothing visible changed.
        _engine.Handle(Select(LightsId, 2), Snapshot(false, true));
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(_engine.Observe(Snapshot(false, true)));

        Assert.Null(_engine.NextDeadline);
        Assert.Equal(2, PositionOf(_engine, LightsId));
    }

    [Fact]
    public void A_press_while_the_game_is_not_listening_starts_no_pause_and_moves_nothing()
    {
        // Why: a dropped press never happened; it must not freeze keyboard inference for a second.
        var result = _engine.Handle(Select(LightsId, 3), Snapshot(true, false));

        Assert.Equal(HandleOutcome.NotListening, result.Outcome);
        Assert.Null(_engine.NextDeadline);
        Assert.Equal(0, PositionOf(_engine, LightsId));
    }

    [Fact]
    public void Resetting_the_positions_clears_the_pause()
    {
        // Why: a profile switch resets the knob; a pause left over would block inference on the fresh knob.
        var profile = new ProfileEngine(ProfileOf(Game.Ets2, LightsWidget()), ScsCatalog(), _time);
        var layout = profile.ForTemplate(Template)!;
        layout.Handle(Select(LightsId, 3), LightsTele(0, 0, 0));

        profile.ResetSwitchPositions();

        Assert.Null(layout.NextDeadline);
        Assert.True(layout.Observe(LightsTele(1, 1, 0)));
        Assert.Equal(2, PositionOf(layout, LightsId));
    }

    [Fact]
    public void A_headlights_widget_can_still_show_a_lit_binding()
    {
        // Why: decisions §5 allows 'lit' on headlights and wipers; only engineStartStop owns its lights.
        var widget = LightsWidget();
        widget.State = new StateBinding { Lit = "truck.light.beam.low" };
        var engine = NewEngine(_time, widget);

        var state = engine.Project(LightsTele(1, 1, 0)).Widgets[LightsId];

        Assert.True(state.Lit);
    }
}
