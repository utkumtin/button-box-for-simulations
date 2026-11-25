using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using Xunit;
using static ButtonBox.Core.Tests.Engine.BehaviorFixture;
using static ButtonBox.Core.Tests.Engine.EngineFixture;

namespace ButtonBox.Core.Tests.Engine;

// Decisions §5 "Widget behaviours": the layout still lists the real actions and the host checks they match the behaviour. A profile
// that does not fit is refused when it loads, so the engine can rely on the shape.
public class BehaviorProfileCheckTests
{
    private static IReadOnlyList<string> Problems(Profile profile, Catalog? catalog = null) =>
        ProfileCheck.Problems(profile, catalog ?? ScsCatalog());

    private static void AssertRefused(Profile profile, Catalog catalog, string contains)
    {
        var problems = Problems(profile, catalog);
        Assert.Single(problems);
        Assert.Contains(contains, problems[0], StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new ProfileEngine(profile, catalog));
    }

    private static void AssertRefused(Widget widget, string contains) =>
        AssertRefused(ProfileOf(Game.Ets2, widget), ScsCatalog(), contains);

    [Fact]
    public void The_three_presets_in_their_valid_shape_have_no_problems()
    {
        Assert.Empty(Problems(ProfileOf(Game.Ets2, LightsWidget(), WipersWidget(), EngineWidget())));
        Assert.Empty(Problems(ProfileOf(Game.Ats, LightsWidget(), WipersWidget(), EngineWidget()), ScsCatalog(Game.Ats)));
    }

    [Fact]
    public void Labels_angles_and_a_lit_binding_stay_editable_on_headlights_and_wipers()
    {
        var lights = LightsWidget();
        lights.Label = "Fahrlicht";
        lights.State = new StateBinding { Lit = "truck.light.beam.low" };
        var wipers = WipersWidget();
        wipers.State = new StateBinding { Lit = "truck.wipers" };

        Assert.Empty(Problems(ProfileOf(Game.Ets2, lights, wipers)));
    }

    [Fact]
    public void Headlights_need_exactly_four_positions()
    {
        AssertRefused(LightsWidget("light-off", "light-park", "light-on"), "exactly 4 positions");
        AssertRefused(LightsWidget("light-off", "light-park", "light-on", "light-on", "light-on"), "exactly 4 positions");
    }

    [Theory]
    [InlineData(0, "light-park")]
    [InlineData(1, "light-off")]
    [InlineData(2, "light-park")]
    [InlineData(3, "light-off")]
    public void Headlights_refuse_a_position_with_the_wrong_action(int position, string wrong)
    {
        string[] actions = ["light-off", "light-park", "light-on", "light-on"];
        actions[position] = wrong;

        AssertRefused(LightsWidget(actions), $"position {position}");
    }

    [Fact]
    public void Headlights_need_the_high_beam_in_the_catalog()
    {
        AssertRefused(ProfileOf(Game.Ets2, LightsWidget()), ScsCatalog(Game.Ets2, "high-beam"), "'high-beam'");
    }

    [Fact]
    public void A_behaviour_switch_refuses_cycle_with_one_problem()
    {
        var lights = LightsWidget();
        lights.Cycle = "light-on";

        AssertRefused(lights, "does not allow 'cycle'");
    }

    [Fact]
    public void Wipers_need_exactly_four_positions()
    {
        AssertRefused(WipersWidget("wipers-off", "wipers-auto", "wipers-low"), "exactly 4 positions");
    }

    [Theory]
    [InlineData(0, "wipers-auto")]
    [InlineData(1, "wipers-off")]
    [InlineData(2, "wipers-fast")]
    [InlineData(3, "wipers-low")]
    public void Wipers_refuse_a_position_with_the_wrong_action(int position, string wrong)
    {
        string[] actions = ["wipers-off", "wipers-auto", "wipers-low", "wipers-fast"];
        actions[position] = wrong;

        AssertRefused(WipersWidget(actions), $"position {position}");
    }

    [Fact]
    public void Engine_start_stop_refuses_another_tap_or_hold_action()
    {
        AssertRefused(EngineWidget(tap: "ignition-off"), "tap action 'ignition-on'");
        AssertRefused(EngineWidget(hold: "ignition-off"), "hold action 'starter'");
    }

    [Fact]
    public void Engine_start_stop_refuses_starter_takeover()
    {
        AssertRefused(EngineWidget(mode: DualActionWidgetHoldMode.StarterTakeover), "holdMode 'fire'");
    }

    [Fact]
    public void Engine_start_stop_needs_ignition_off_in_the_catalog()
    {
        AssertRefused(ProfileOf(Game.Ets2, EngineWidget()), ScsCatalog(Game.Ets2, "ignition-off"), "'ignition-off'");
    }

    [Fact]
    public void Engine_start_stop_refuses_a_state_binding()
    {
        // Why: its lit and LED come from the behaviour; a binding would be a second, conflicting source.
        var engine = EngineWidget();
        engine.State = new StateBinding { Lit = "truck.electric.enabled" };

        AssertRefused(engine, "'state' binding is not allowed");
    }

    [Fact]
    public void Only_ets2_and_ats_profiles_may_use_a_behaviour()
    {
        var vjoy = Catalog(Game.Acc, ActionIds);

        AssertRefused(ProfileOf(Game.Acc, LightsWidget()), vjoy, "only available for ETS2 and ATS");
        AssertRefused(ProfileOf(Game.Acc, WipersWidget()), vjoy, "only available for ETS2 and ATS");
        AssertRefused(ProfileOf(Game.Acc, EngineWidget()), vjoy, "only available for ETS2 and ATS");
    }

    [Fact]
    public void Widgets_without_a_behaviour_are_checked_exactly_as_before()
    {
        // Why: profiles without the field keep the generic behaviour, including 3 positions and the generic engine button.
        var lights = Switch(LightsId, "light-off", "light-park", "light-on");
        var engine = Dual(EngineId, "ignition-on", "starter");
        engine.Cell = Cell(3, 0);
        lights.Cell = Cell(0, 0);

        Assert.Empty(Problems(ProfileOf(Game.Ets2, lights, engine)));
    }
}
