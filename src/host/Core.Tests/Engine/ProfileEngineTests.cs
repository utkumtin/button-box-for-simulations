using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using Xunit;
using G = ButtonBox.Core.Contracts.Gesture;
using static ButtonBox.Core.Tests.Engine.EngineFixture;

namespace ButtonBox.Core.Tests.Engine;

// Publishing (decisions §3): a Save keeps a switch's commanded position only when the switch is unchanged. A wrong carry-over would
// show the phone a position the game is not in, so each rule has its own test.
public class ProfileEngineTests
{
    private const string OtherTemplate = "tablet-b";

    private static SwitchWidget SwitchAt(string id, int row, params string?[] actions)
    {
        var sw = Switch(id, actions);
        sw.Cell = Cell(0, row);
        return sw;
    }

    private static ProfileEngine Engine(params Layout[] layouts) => new(Profile(Game.Ets2, layouts), Catalog());

    private static Layout TwoSwitches(string template, int lightsPositions, string otherId = "fog") =>
        Layout(template, 4, 3, Toggle(), SwitchAt("lights", 1, ThreePositions[..lightsPositions]), SwitchAt(otherId, 2));

    private static readonly string[] ThreePositions = ["lights-off", "lights-park", "lights-low"];

    private static int? PositionOf(ProfileEngine engine, string id, string template = Template) =>
        engine.ForTemplate(template)!.Project(Listening).Widgets[id].Position;

    private static void Choose(ProfileEngine engine, string id, int position, string template = Template) =>
        Assert.Equal(HandleOutcome.Accepted, engine.ForTemplate(template)!.Handle(Select(id, position), Listening).Outcome);

    [Fact]
    public void A_switch_with_the_same_id_and_number_of_positions_keeps_its_position()
    {
        var before = Engine(TwoSwitches(Template, 3));
        Choose(before, "lights", 2);
        var after = Engine(TwoSwitches(Template, 3));

        after.CarryPositionsFrom(before);

        Assert.Equal(2, PositionOf(after, "lights"));
    }

    [Fact]
    public void A_switch_whose_number_of_positions_changed_resets_to_zero()
    {
        // Position 2 of 3 means nothing in a 2-position switch (and position 1 of 3 is not position 1 of 2): the game's real state is unknown.
        var before = Engine(TwoSwitches(Template, 3));
        Choose(before, "lights", 2);
        Choose(before, "fog", 1);
        var after = Engine(TwoSwitches(Template, 2));

        after.CarryPositionsFrom(before);

        Assert.Equal(0, PositionOf(after, "lights"));
        Assert.Equal(1, PositionOf(after, "fog")); // the unchanged switch next to it still carries over
    }

    [Fact]
    public void A_new_switch_starts_at_zero_and_a_switch_only_in_the_old_engine_is_ignored()
    {
        var before = Engine(TwoSwitches(Template, 3, otherId: "fog"));
        Choose(before, "fog", 2);
        Choose(before, "lights", 1);
        var after = Engine(TwoSwitches(Template, 3, otherId: "rear-fog"));

        after.CarryPositionsFrom(before);

        Assert.Equal(1, PositionOf(after, "lights"));
        Assert.Equal(0, PositionOf(after, "rear-fog"));
        Assert.DoesNotContain("fog", after.ForTemplate(Template)!.Project(Listening).Widgets.Keys);
    }

    [Fact]
    public void A_template_that_is_not_in_the_old_engine_is_left_alone_and_a_template_only_in_the_old_one_is_ignored()
    {
        var before = Engine(TwoSwitches(Template, 3), TwoSwitches(OtherTemplate, 3));
        Choose(before, "lights", 2, OtherTemplate);
        var after = Engine(TwoSwitches(Template, 3));

        after.CarryPositionsFrom(before);

        Assert.Equal(0, PositionOf(after, "lights"));
        Assert.Null(after.ForTemplate(OtherTemplate));

        // The other direction: the old engine has no layout for the new template, so its switches start at 0.
        var third = Engine(TwoSwitches(Template, 3), TwoSwitches(OtherTemplate, 3));
        Choose(after, "lights", 1);
        third.CarryPositionsFrom(after);
        Assert.Equal(1, PositionOf(third, "lights"));
        Assert.Equal(0, PositionOf(third, "lights", OtherTemplate));
    }

    [Fact]
    public void Carrying_over_does_not_change_the_old_engine()
    {
        // The old engine may still be handling a gesture; carrying over must only read it.
        var before = Engine(TwoSwitches(Template, 3));
        Choose(before, "lights", 2);
        var after = Engine(TwoSwitches(Template, 3));

        after.CarryPositionsFrom(before);
        Choose(after, "lights", 0);

        Assert.Equal(2, PositionOf(before, "lights"));
    }

    [Fact]
    public void ResetSwitchPositions_sends_every_switch_back_to_zero()
    {
        // A profile switch resets all commanded positions, also when the engine is one that was used before.
        var engine = Engine(TwoSwitches(Template, 3), TwoSwitches(OtherTemplate, 3));
        Choose(engine, "lights", 2);
        Choose(engine, "fog", 1, OtherTemplate);

        engine.ResetSwitchPositions();

        Assert.Equal(0, PositionOf(engine, "lights"));
        Assert.Equal(0, PositionOf(engine, "fog", OtherTemplate));
    }

    [Fact]
    public void TryGetOutput_maps_scs_and_vjoy_catalog_actions_and_rejects_unknown_ids()
    {
        var scs = new ProfileEngine(Profile(Game.Ets2), Catalog(Game.Ets2));
        Assert.True(scs.TryGetOutput("horn", out var input));
        Assert.Equal(new ScsInput("in-horn"), input);

        var vjoy = new ProfileEngine(Profile(Game.Acc), Catalog(Game.Acc));
        Assert.True(vjoy.TryGetOutput("wiper-up", out var button));
        Assert.Equal(new VJoyButton(3), button); // third action of the fixture catalog

        // Test mode must not turn an arbitrary string into a game output: only catalog actions exist.
        Assert.False(scs.TryGetOutput("nope", out _));
        Assert.False(scs.TryGetOutput("in-horn", out _)); // the game-side input name is not an action id
    }
}
