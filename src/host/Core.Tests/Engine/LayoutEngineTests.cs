using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using Xunit;
using G = ButtonBox.Core.Contracts.Gesture;
using static ButtonBox.Core.Tests.Engine.EngineFixture;

namespace ButtonBox.Core.Tests.Engine;

// The phone names only a widget and a gesture. These tests pin that the host, not the phone, decides
// what reaches the game: anything that does not fit the layout is dropped.
public class LayoutEngineTests
{
    private static void AssertInvalid(HandleResult result)
    {
        Assert.Equal(HandleOutcome.Invalid, result.Outcome);
        Assert.Empty(result.Outputs);
        Assert.False(result.StateChanged);
    }

    private static void AssertSends(HandleResult result, params string[] names)
    {
        Assert.Equal(HandleOutcome.Accepted, result.Outcome);
        Assert.Equal(names.Select(n => (GameOutput)new ScsInput(n)), result.Outputs);
    }

    [Fact]
    public void Toggle_tap_sends_its_action()
    {
        AssertSends(NewEngine().Handle(Msg("horn", G.Tap), Listening), "in-horn");
    }

    [Fact]
    public void Encoder_minus_and_plus_send_their_own_actions()
    {
        var engine = NewEngine();
        AssertSends(engine.Handle(Msg("wiper", G.Minus), Listening), "in-wiper-down");
        AssertSends(engine.Handle(Msg("wiper", G.Plus), Listening), "in-wiper-up");
    }

    [Fact]
    public void Dual_tap_and_hold_send_their_actions()
    {
        var engine = NewEngine();
        AssertSends(engine.Handle(Msg("engine", G.Tap), Listening), "in-ign");
        AssertSends(engine.Handle(Msg("engine", G.Hold), Listening), "in-starter");
    }

    [Fact]
    public void Vjoy_games_map_to_vjoy_buttons()
    {
        var engine = NewEngine(Profile(Game.Acc), Catalog(Game.Acc));
        var result = engine.Handle(Msg("horn", G.Tap), Listening);
        Assert.Equal([new VJoyButton(1)], result.Outputs);
    }

    [Fact]
    public void Switch_select_sends_that_positions_action_and_commands_it()
    {
        var engine = NewEngine();
        var result = engine.Handle(Select("lights", 2), Listening);
        AssertSends(result, "in-lights-low");
        Assert.True(result.StateChanged);
        Assert.Equal(2, engine.Project(Listening).Widgets["lights"].Position);
    }

    [Fact]
    public void Switch_select_of_the_current_position_still_sends()
    {
        // The phone only sends changes; if it sends the same one, the game may be out of step, so we resend.
        var engine = NewEngine();
        var result = engine.Handle(Select("lights", 0), Listening);
        AssertSends(result, "in-lights-off");
        Assert.False(result.StateChanged);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void Switch_select_out_of_range_is_invalid(int position)
    {
        var engine = NewEngine();
        AssertInvalid(engine.Handle(Select("lights", position), Listening));
        Assert.Equal(0, engine.Project(Listening).Widgets["lights"].Position);
    }

    [Fact]
    public void Switch_next_and_previous_step_one_position()
    {
        var engine = NewEngine();
        var next = engine.Handle(Msg("lights", G.Next), Listening);
        AssertSends(next, "in-lights-park");
        Assert.True(next.StateChanged);

        var previous = engine.Handle(Msg("lights", G.Previous), Listening);
        AssertSends(previous, "in-lights-off");
        Assert.True(previous.StateChanged);
    }

    [Fact]
    public void Switch_previous_at_the_first_position_does_nothing()
    {
        // No wrap: a stray tap on the left of the first position must not jump to the last one.
        var engine = NewEngine();
        var result = engine.Handle(Msg("lights", G.Previous), Listening);
        Assert.Equal(HandleOutcome.Accepted, result.Outcome);
        Assert.Empty(result.Outputs);
        Assert.False(result.StateChanged);
        Assert.Equal(0, engine.Project(Listening).Widgets["lights"].Position);
    }

    [Fact]
    public void Switch_next_at_the_last_position_does_nothing()
    {
        var engine = NewEngine();
        engine.Handle(Select("lights", 2), Listening);
        var result = engine.Handle(Msg("lights", G.Next), Listening);
        Assert.Equal(HandleOutcome.Accepted, result.Outcome);
        Assert.Empty(result.Outputs);
        Assert.False(result.StateChanged);
        Assert.Equal(2, engine.Project(Listening).Widgets["lights"].Position);
    }

    [Fact]
    public void Devices_with_the_same_template_share_switch_positions()
    {
        // Two phones with one template drive one panel: the second must see where the first left the switch.
        var profileEngine = new ProfileEngine(Profile(), Catalog());
        profileEngine.ForTemplate(Template)!.Handle(Select("lights", 1), Listening);
        var other = profileEngine.ForTemplate(Template)!;
        Assert.Same(profileEngine.ForTemplate(Template), other);
        Assert.Equal(1, other.Project(Listening).Widgets["lights"].Position);
    }

    [Fact]
    public void Unknown_template_has_no_engine()
    {
        Assert.Null(new ProfileEngine(Profile(), Catalog()).ForTemplate("other"));
    }

    [Fact]
    public void Unknown_widget_is_invalid()
    {
        // A stolen token must only reach widgets that exist in the active layout.
        AssertInvalid(NewEngine().Handle(Msg("ghost", G.Tap), Listening));
        AssertInvalid(NewEngine().Handle(Select("ghost", 0), Listening));
    }

    [Fact]
    public void Hello_is_invalid()
    {
        AssertInvalid(NewEngine().Handle(new HelloMessage { Token = new string('A', 43) }, Listening));
    }

    [Theory]
    [InlineData("horn", G.Hold)]
    [InlineData("horn", G.Minus)]
    [InlineData("horn", G.Next)]
    [InlineData("wiper", G.Tap)]
    [InlineData("wiper", G.Next)]
    [InlineData("lights", G.Tap)]
    [InlineData("lights", G.Hold)]
    [InlineData("lights", G.Plus)]
    [InlineData("engine", G.Next)]
    [InlineData("engine", G.Plus)]
    public void Gesture_that_does_not_fit_the_widget_type_is_invalid(string widget, G gesture)
    {
        AssertInvalid(NewEngine().Handle(Msg(widget, gesture), Listening));
    }

    [Fact]
    public void Select_on_a_non_switch_is_invalid()
    {
        AssertInvalid(NewEngine().Handle(Select("horn", 0), Listening));
    }

    [Fact]
    public void Dual_hold_without_hold_action_is_invalid()
    {
        var engine = NewEngine(Profile(Game.Ets2, Layout(Template, 4, 1, Dual(hold: null))));
        AssertInvalid(engine.Handle(Msg("engine", G.Hold), Listening));
        AssertSends(engine.Handle(Msg("engine", G.Tap), Listening), "in-ign");
    }

    [Fact]
    public void Dual_tap_without_tap_action_is_invalid()
    {
        var engine = NewEngine(Profile(Game.Ets2, Layout(Template, 4, 1, Dual(tap: null))));
        AssertInvalid(engine.Handle(Msg("engine", G.Tap), Listening));
    }

    [Fact]
    public void Not_listening_drops_valid_gestures_without_outputs_or_position_change()
    {
        // Presses while the game is not accepting input are dropped, never queued, and must not move
        // the commanded switch position, or the phone would show a position the game never received.
        var engine = NewEngine();
        var silent = Snapshot(true, false);
        foreach (var message in new ClientMessage[]
        {
            Msg("horn", G.Tap),
            Msg("wiper", G.Plus),
            Msg("engine", G.Hold),
            Msg("lights", G.Next),
            Select("lights", 2),
        })
        {
            var result = engine.Handle(message, silent);
            Assert.Equal(HandleOutcome.NotListening, result.Outcome);
            Assert.Empty(result.Outputs);
            Assert.False(result.StateChanged);
        }
        Assert.Equal(0, engine.Project(silent).Widgets["lights"].Position);
    }

    [Fact]
    public void Invalid_gesture_is_reported_invalid_even_when_not_listening()
    {
        var silent = Snapshot(false, false);
        AssertInvalid(NewEngine().Handle(Msg("ghost", G.Tap), silent));
        AssertInvalid(NewEngine().Handle(Select("lights", 9), silent));
    }

    [Fact]
    public async Task Concurrent_next_gestures_never_lose_or_overshoot_a_step()
    {
        var engine = NewEngine(Profile(Game.Ets2, Layout(Template, 4, 1, Switch("s", "horn", "wiper-down", "wiper-up", "lights-off", "lights-park", "lights-low"))));
        var moved = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            if (engine.Handle(Msg("s", G.Next), Listening).StateChanged)
            {
                Interlocked.Increment(ref moved);
            }
        })));
        Assert.Equal(5, moved);
        Assert.Equal(5, engine.Project(Listening).Widgets["s"].Position);
    }

    [Fact]
    public void Project_reports_lit_and_led_from_their_own_fields()
    {
        var snapshot = Snapshot(true, true, ("horn.lit", 1), ("ign.lit", 0), ("eng.led", 7), ("lights.led", 0));
        var widgets = NewEngine().Project(snapshot).Widgets;

        Assert.True(widgets["horn"].Lit);
        Assert.Null(widgets["horn"].Led);
        Assert.False(widgets["engine"].Lit);
        Assert.Equal(Led.Green, widgets["engine"].Led);
        Assert.Equal(Led.Off, widgets["lights"].Led);
        Assert.Null(widgets["lights"].Lit);
    }

    [Fact]
    public void Project_leaves_out_fields_the_snapshot_does_not_have()
    {
        var widgets = NewEngine().Project(Snapshot(true, true, ("horn.lit", 1))).Widgets;

        Assert.Null(widgets["lights"].Led);
        Assert.False(widgets.ContainsKey("engine"));
        Assert.False(widgets.ContainsKey("wiper"));
    }

    [Fact]
    public void Project_without_telemetry_reports_only_switch_positions()
    {
        // No telemetry means dim and neutral on the phone: the host must not invent lit or LED values.
        var widgets = NewEngine().Project(Snapshot(false, false, ("horn.lit", 1), ("lights.led", 1))).Widgets;

        Assert.Equal(["lights"], widgets.Keys);
        Assert.Equal(0, widgets["lights"].Position);
        Assert.Null(widgets["lights"].Led);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Status_reports_availability_and_listening(bool available, bool listening)
    {
        var status = NewEngine().Status(Snapshot(available, listening));
        Assert.Equal(available, status.Telemetry);
        Assert.Equal(listening, status.GameListening);
    }
}
