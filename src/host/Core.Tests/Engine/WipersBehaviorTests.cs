using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using Xunit;
using static ButtonBox.Core.Tests.Engine.BehaviorFixture;
using static ButtonBox.Core.Tests.Engine.EngineFixture;

namespace ButtonBox.Core.Tests.Engine;

// Decisions §5.3: the wiper knob follows truck.wipers (on/off only) as far as it can tell, with the headlights' inference pause.
public class WipersBehaviorTests
{
    private readonly ManualTime _time = new();
    private readonly LayoutEngine _engine;

    public WipersBehaviorTests() => _engine = NewEngine(_time, WipersWidget());

    private static TelemetrySnapshot Flag(int on) => Tele(("truck.wipers", on));

    // Turns the knob and lets telemetry catch up, so the pause is over.
    private void Settle(int position)
    {
        _engine.Handle(Select(WipersId, position), Flag(position == 0 ? 0 : 1));
        _engine.Observe(Flag(position == 0 ? 0 : 1));
    }

    [Fact]
    public void Positions_send_their_own_actions()
    {
        Assert.Equal(["in-wipers-fast"], Names(_engine.Handle(Select(WipersId, 3), Listening)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Wipers_turned_off_from_the_keyboard_move_the_knob_to_off(int from)
    {
        // Why: the device test showed the flag reports off only when the wipers are off, also from AUTO.
        Settle(from);

        Assert.True(_engine.Observe(Flag(0)));

        Assert.Equal(0, PositionOf(_engine, WipersId));
    }

    [Fact]
    public void Wipers_turned_on_from_the_keyboard_move_the_knob_from_off_to_auto()
    {
        // Why: the game's wiper key reaches AUTO first (device test); the knob must not stay on OFF while the wipers run.
        Settle(0);

        Assert.True(_engine.Observe(Flag(1)));

        Assert.Equal(1, PositionOf(_engine, WipersId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void While_the_wipers_run_the_knob_stays_where_it_is(int position)
    {
        // Why: the flag cannot tell AUTO, LOW and FAST apart; following it further would be a guess.
        Settle(position);

        Assert.False(_engine.Observe(Flag(1)));

        Assert.Equal(position, PositionOf(_engine, WipersId));
    }

    [Fact]
    public void A_knob_turned_from_off_is_not_moved_back_before_telemetry_catches_up()
    {
        // Why: the flag is still 0 for a moment after the command; without the pause the knob would flick back to OFF.
        Settle(0);
        _engine.Handle(Select(WipersId, 2), Flag(0));

        Assert.False(_engine.Observe(Flag(0)));
        Assert.Equal(2, PositionOf(_engine, WipersId));

        Assert.False(_engine.Observe(Flag(1))); // matches: the pause ends, LOW stays
        Assert.Equal(2, PositionOf(_engine, WipersId));
        Assert.Null(_engine.NextDeadline);
    }

    [Fact]
    public void A_knob_turned_to_off_does_not_jump_to_auto_while_the_wipers_are_still_running()
    {
        // Why: the flag is still 1 for a moment after wipers0; inference alone would show AUTO.
        Settle(3);
        _engine.Handle(Select(WipersId, 0), Flag(1));

        Assert.False(_engine.Observe(Flag(1)));
        Assert.Equal(0, PositionOf(_engine, WipersId));
    }

    [Fact]
    public void A_pause_that_telemetry_never_satisfies_ends_after_one_second()
    {
        // Why: if the game ignores the command, the knob must show what the game does instead of staying wrong.
        Settle(0);
        _engine.Handle(Select(WipersId, 2), Flag(0));
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(1), _engine.NextDeadline);

        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(_engine.Observe(Flag(0)));
        Assert.Equal(2, PositionOf(_engine, WipersId));

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(_engine.Observe(Flag(0)));
        Assert.Equal(0, PositionOf(_engine, WipersId));
        Assert.Null(_engine.NextDeadline);
    }

    [Fact]
    public void Without_telemetry_the_knob_keeps_its_position()
    {
        // Why: no data is not "off"; the knob keeps the commanded position instead of snapping to a guess.
        _engine.Handle(Select(WipersId, 2), Snapshot(false, true));
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(_engine.Observe(Snapshot(false, true)));
        Assert.False(_engine.Observe(Tele(("truck.light.parking", 1))));

        Assert.Equal(2, PositionOf(_engine, WipersId));
    }

    [Fact]
    public void A_press_while_the_game_is_not_listening_starts_no_pause()
    {
        // Why: a dropped press never happened; it must not freeze the keyboard follow for a second.
        var result = _engine.Handle(Select(WipersId, 2), Snapshot(true, false));

        Assert.Equal(HandleOutcome.NotListening, result.Outcome);
        Assert.Null(_engine.NextDeadline);
    }

    [Fact]
    public void Resetting_the_positions_clears_the_pause()
    {
        // Why: a profile switch resets the knob; a leftover pause would block the follow on the fresh knob.
        var profile = new ProfileEngine(ProfileOf(Game.Ets2, WipersWidget()), ScsCatalog(), _time);
        var layout = profile.ForTemplate(Template)!;
        layout.Handle(Select(WipersId, 2), Flag(0));

        profile.ResetSwitchPositions();

        Assert.Null(layout.NextDeadline);
        Assert.True(layout.Observe(Flag(1)));
        Assert.Equal(1, PositionOf(layout, WipersId));
    }
}
