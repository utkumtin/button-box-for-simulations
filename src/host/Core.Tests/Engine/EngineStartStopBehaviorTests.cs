using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using Xunit;
using G = ButtonBox.Core.Contracts.Gesture;
using static ButtonBox.Core.Tests.Engine.BehaviorFixture;
using static ButtonBox.Core.Tests.Engine.EngineFixture;

namespace ButtonBox.Core.Tests.Engine;

// Decisions §5.2: one button starts and stops the engine. What a tap or a hold sends depends on the truck's state, and the amber LED
// reports a start that did not work.
public class EngineStartStopBehaviorTests
{
    private static readonly TimeSpan Amber = TimeSpan.FromSeconds(4);

    private readonly ManualTime _time = new();
    private readonly LayoutEngine _engine;

    public EngineStartStopBehaviorTests() => _engine = NewEngine(_time, EngineWidget());

    private HandleResult Press(G gesture, TelemetrySnapshot telemetry) => _engine.Handle(Msg(EngineId, gesture), telemetry);

    private WidgetState Shown(TelemetrySnapshot telemetry) => _engine.Project(telemetry).Widgets[EngineId];

    [Theory]
    [InlineData(G.Tap, 0, 0, new[] { "in-ignition-on" })] // all off: electric on
    [InlineData(G.Tap, 1, 0, new[] { "in-ignition-off" })] // electric on, engine off: electric off
    [InlineData(G.Tap, 1, 1, new string[0])] // engine running: a stray touch never stops it
    [InlineData(G.Hold, 1, 0, new[] { "in-starter" })] // electric on: starter
    [InlineData(G.Hold, 0, 0, new[] { "in-ignition-on", "in-starter" })] // all off: electric on, then starter, as one gesture
    [InlineData(G.Hold, 1, 1, new[] { "in-ignition-off" })] // running: engine and electric off
    public void Tap_and_hold_matrix(G gesture, int electric, int running, string[] expected)
    {
        var result = Press(gesture, EngineTele(electric, running));

        Assert.Equal(HandleOutcome.Accepted, result.Outcome);
        Assert.Equal(expected, Names(result));
    }

    [Fact]
    public void A_tap_while_the_engine_runs_changes_nothing_and_keeps_a_pending_timer()
    {
        // Why: it must be a true no-op: no outputs, no state frame, and the pending attempt is not disturbed.
        Press(G.Hold, EngineTele(1, 0));
        var deadline = _engine.NextDeadline;

        var tap = Press(G.Tap, EngineTele(1, 1));

        Assert.Equal(HandleOutcome.Accepted, tap.Outcome);
        Assert.Empty(tap.Outputs);
        Assert.False(tap.StateChanged);
        Assert.Equal(deadline, _engine.NextDeadline);
    }

    [Fact]
    public void Electric_turned_off_from_the_keyboard_clears_amber_and_a_pending_attempt()
    {
        // Why (§5.2 "electric off clears all lights"): a dark button with an amber LED would report a failed start the user
        // already abandoned by switching the truck off.
        var on = EngineTele(1, 0);
        _engine.Observe(on);
        Press(G.Hold, on);
        _time.Advance(Amber);
        _engine.Observe(on);
        Assert.Equal(Led.Amber, Shown(on).Led);

        Assert.True(_engine.Observe(EngineTele(0, 0)));
        Assert.Equal(Led.Off, Shown(EngineTele(0, 0)).Led);

        // A new attempt, then the keyboard switches off before the timeout: no amber later.
        _engine.Observe(on);
        Press(G.Hold, on);
        _engine.Observe(EngineTele(0, 0));
        Assert.Null(_engine.NextDeadline);
    }

    [Fact]
    public void Electric_still_off_right_after_a_hold_from_all_off_does_not_cancel_the_attempt()
    {
        // Why: the hold sends ignitionon + starter; telemetry may report another change before electric comes on.
        var off = EngineTele(0, 0);
        _engine.Observe(off);
        Press(G.Hold, off);

        _engine.Observe(Tele(("truck.electric.enabled", 0), ("truck.engine.enabled", 0), ("truck.light.beacon", 1)));
        _time.Advance(Amber);
        Assert.True(_engine.Observe(EngineTele(1, 0)));
        Assert.Equal(Led.Amber, Shown(EngineTele(1, 0)).Led);
    }

    [Fact]
    public void Without_telemetry_the_truck_counts_as_all_off_so_a_gesture_never_stops_the_engine()
    {
        var none = Snapshot(false, true);

        Assert.Equal(["in-ignition-on"], Names(Press(G.Tap, none)));
        Assert.Equal(["in-ignition-on", "in-starter"], Names(Press(G.Hold, none)));
    }

    [Fact]
    public void Amber_lights_four_seconds_after_the_gesture_that_sent_the_starter_and_not_before()
    {
        // Why: the engine takes about 1.3 s to start; amber means "it did not". Both too early and too late are wrong.
        var tele = EngineTele(1, 0);
        var accepted = _time.GetUtcNow();
        Press(G.Hold, tele);
        Assert.Equal(accepted + Amber, _engine.NextDeadline);

        _time.Advance(Amber - TimeSpan.FromMilliseconds(1));
        Assert.False(_engine.Observe(tele));
        Assert.Equal(Led.Off, Shown(tele).Led);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(_engine.Observe(tele));
        Assert.Equal(Led.Amber, Shown(tele).Led);
        Assert.Null(_engine.NextDeadline); // handled: the session timer must not fire again for it
    }

    [Fact]
    public void A_start_that_works_shows_green_and_never_goes_amber()
    {
        var off = EngineTele(1, 0);
        Press(G.Hold, off);

        var running = EngineTele(1, 1);
        Assert.False(_engine.Observe(running));
        Assert.Equal(Led.Green, Shown(running).Led);

        _time.Advance(Amber);
        Assert.False(_engine.Observe(running));
        Assert.Null(_engine.NextDeadline);
        Assert.Equal(Led.Green, Shown(running).Led);
    }

    [Fact]
    public void The_next_hold_clears_amber_and_starts_a_new_attempt()
    {
        var tele = EngineTele(1, 0);
        Press(G.Hold, tele);
        _time.Advance(Amber);
        _engine.Observe(tele);
        Assert.Equal(Led.Amber, Shown(tele).Led);

        var hold = Press(G.Hold, tele);

        Assert.True(hold.StateChanged);
        Assert.Equal(Led.Off, Shown(tele).Led);
        Assert.Equal(_time.GetUtcNow() + Amber, _engine.NextDeadline);
    }

    [Fact]
    public void A_second_hold_during_an_attempt_restarts_the_timer()
    {
        var tele = EngineTele(1, 0);
        Press(G.Hold, tele);
        _time.Advance(TimeSpan.FromSeconds(3));
        Press(G.Hold, tele);

        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.False(_engine.Observe(tele)); // 6 s after the first, 3 s after the second

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_engine.Observe(tele));
    }

    [Fact]
    public void A_gesture_that_turns_electric_off_clears_amber_and_cancels_a_pending_timer()
    {
        // Why: after the user switched everything off, amber must not light up four seconds later.
        var tele = EngineTele(1, 0);
        Press(G.Hold, tele);
        _time.Advance(Amber);
        _engine.Observe(tele);
        Assert.Equal(Led.Amber, Shown(tele).Led);

        var tap = Press(G.Tap, tele);
        Assert.Equal(["in-ignition-off"], Names(tap));
        Assert.True(tap.StateChanged);
        Assert.Equal(Led.Off, Shown(tele).Led);

        // And the pending case: a hold, then a tap that switches electric off before the timer ends.
        Press(G.Hold, tele);
        Press(G.Tap, tele);
        Assert.Null(_engine.NextDeadline);
        _time.Advance(Amber * 2);
        Assert.False(_engine.Observe(tele));
        Assert.Equal(Led.Off, Shown(tele).Led);
    }

    [Fact]
    public void A_hold_that_stops_a_running_engine_clears_a_pending_timer()
    {
        var tele = EngineTele(1, 0);
        Press(G.Hold, tele);

        Press(G.Hold, EngineTele(1, 1));

        Assert.Null(_engine.NextDeadline);
    }

    [Fact]
    public void A_running_engine_clears_amber_and_a_later_stop_does_not_bring_it_back()
    {
        // Why: a later start supersedes the failed attempt; amber is about that attempt only.
        var tele = EngineTele(1, 0);
        Press(G.Hold, tele);
        _time.Advance(Amber);
        _engine.Observe(tele);

        Assert.True(_engine.Observe(EngineTele(1, 1)));
        Assert.False(_engine.Observe(EngineTele(1, 0)));

        Assert.Equal(Led.Off, Shown(tele).Led);
    }

    [Fact]
    public void Lit_follows_the_electric_flag_and_the_led_is_off_when_idle()
    {
        Assert.True(Shown(EngineTele(1, 0)).Lit);
        Assert.False(Shown(EngineTele(0, 0)).Lit);
        Assert.Equal(Led.Off, Shown(EngineTele(0, 0)).Led);
        Assert.Equal(Led.Green, Shown(EngineTele(1, 1)).Led);
    }

    [Fact]
    public void Without_telemetry_there_is_no_lit_and_no_led_but_amber_is_kept_for_when_it_returns()
    {
        // Why: no data means dim, like every widget. The failed attempt is still a fact once data is back.
        var none = Snapshot(false, true);
        Press(G.Hold, none);
        _time.Advance(Amber);

        Assert.True(_engine.Observe(none));
        Assert.DoesNotContain(EngineId, _engine.Project(none).Widgets.Keys);

        Assert.Equal(Led.Amber, Shown(EngineTele(1, 0)).Led);
    }

    [Fact]
    public void A_hold_while_the_game_is_not_listening_changes_nothing()
    {
        // Why: a dropped press never reached the game, so it starts no attempt and does not clear amber.
        var tele = EngineTele(1, 0);
        Press(G.Hold, tele);
        _time.Advance(Amber);
        _engine.Observe(tele);

        var paused = Snapshot(true, false, ("truck.electric.enabled", 1), ("truck.engine.enabled", 0));
        var dropped = Press(G.Hold, paused);

        Assert.Equal(HandleOutcome.NotListening, dropped.Outcome);
        Assert.Equal(Led.Amber, Shown(tele).Led);
        Assert.Null(_engine.NextDeadline);
    }

    [Fact]
    public void Resetting_clears_amber_and_the_timer()
    {
        var profile = new ProfileEngine(ProfileOf(Game.Ets2, EngineWidget()), ScsCatalog(), _time);
        var layout = profile.ForTemplate(Template)!;
        var tele = EngineTele(1, 0);
        layout.Handle(Msg(EngineId, G.Hold), tele);
        _time.Advance(Amber);
        layout.Observe(tele);

        profile.ResetSwitchPositions();

        Assert.Equal(Led.Off, layout.Project(tele).Widgets[EngineId].Led);
        Assert.Null(layout.NextDeadline);
    }

    [Fact]
    public void A_profile_engine_hands_its_clock_to_every_layout_engine()
    {
        // Why: the amber deadline must come from the clock the session was given, or no timer test (and no host wiring) can steer it.
        var other = ProfileOf(Game.Ets2, EngineWidget());
        var second = Layout("tablet", 4, 1, EngineWidget());
        other.Layouts = [.. other.Layouts, second];
        var profile = new ProfileEngine(other, ScsCatalog(), _time);

        Assert.Equal(2, profile.Layouts.Count);
        foreach (var layout in profile.Layouts)
        {
            layout.Handle(Msg(EngineId, G.Hold), EngineTele(1, 0));
            Assert.Equal(_time.GetUtcNow() + Amber, layout.NextDeadline);
        }
    }
}
