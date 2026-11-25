using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using static ButtonBox.Core.Tests.Engine.EngineFixture;

namespace ButtonBox.Core.Tests.Engine;

// Core.Tests has no FakeTimeProvider package, so the behaviour timing tests drive the engine with this hand-moved clock.
// The engine only reads the time; it never creates a timer, so a clock that moves when the test says so is all it needs.
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

// One ets2 profile with the three behaviour presets, built from the real catalog action ids (the engine looks some of them up by id).
internal static class BehaviorFixture
{
    public const string LightsId = "lights";
    public const string WipersId = "wipers";
    public const string EngineId = "engine";

    public static readonly string[] ActionIds =
    [
        "ignition-on", "ignition-off", "starter", "high-beam",
        "light-off", "light-park", "light-on",
        "wipers-off", "wipers-auto", "wipers-low", "wipers-fast",
    ];

    public static Catalog ScsCatalog(Game game = Game.Ets2, params string[] without) =>
        Catalog(game, ActionIds.Except(without).ToArray());

    public static SwitchWidget LightsWidget(params string?[] actions) =>
        Behaving(Switch(LightsId, actions.Length > 0 ? actions : ["light-off", "light-park", "light-on", "light-on"]), SwitchBehavior.Headlights, 0);

    public static SwitchWidget WipersWidget(params string?[] actions) =>
        Behaving(Switch(WipersId, actions.Length > 0 ? actions : ["wipers-off", "wipers-auto", "wipers-low", "wipers-fast"]), SwitchBehavior.Wipers, 1);

    public static DualActionWidget EngineWidget(string? tap = "ignition-on", string? hold = "starter", DualActionWidgetHoldMode mode = DualActionWidgetHoldMode.Fire)
    {
        var widget = Dual(EngineId, tap, hold, mode);
        widget.Cell = Cell(3, 0);
        widget.Behavior = DualActionBehavior.EngineStartStop;
        widget.State = null;
        return widget;
    }

    private static SwitchWidget Behaving(SwitchWidget widget, SwitchBehavior behavior, int col)
    {
        widget.Behavior = behavior;
        widget.Cell = Cell(col, 0);
        widget.State = null;
        return widget;
    }

    public static Profile ProfileOf(Game game, params Widget[] widgets) =>
        Profile(game, Layout(Template, 4, 1, widgets));

    public static LayoutEngine NewEngine(ManualTime time, params Widget[] widgets) =>
        new ProfileEngine(ProfileOf(Game.Ets2, widgets), ScsCatalog(), time).ForTemplate(Template)!;

    public static TelemetrySnapshot Tele(params (string Field, int Value)[] fields) => Snapshot(true, true, fields);

    // parking, low beam, high beam
    public static TelemetrySnapshot LightsTele(int parking, int low, int high) =>
        Tele(("truck.light.parking", parking), ("truck.light.beam.low", low), ("truck.light.beam.high", high));

    public static TelemetrySnapshot EngineTele(int electric, int running) =>
        Tele(("truck.electric.enabled", electric), ("truck.engine.enabled", running));

    public static int PositionOf(LayoutEngine engine, string widget) =>
        engine.Project(Listening).Widgets[widget].Position!.Value;

    public static IEnumerable<string> Names(HandleResult result) => result.Outputs.Select(o => ((ScsInput)o).Name);
}
