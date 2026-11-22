using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;

namespace ButtonBox.Core.Tests.Engine;

// Small ets2 profile with one widget of each type. Tests tweak it through the optional parameters.
internal static class EngineFixture
{
    public const string Template = "phone-a";

    public static readonly TelemetrySnapshot Listening = new(true, true, new Dictionary<string, int>());

    public static TelemetrySnapshot Snapshot(bool available, bool listening, params (string Field, int Value)[] fields) =>
        new(available, listening, fields.ToDictionary(f => f.Field, f => f.Value));

    public static Catalog Catalog(Game game = Game.Ets2, params string[] ids)
    {
        var all = ids.Length > 0 ? ids : ["horn", "wiper-down", "wiper-up", "lights-off", "lights-park", "lights-low", "ign", "starter"];
        return new Catalog
        {
            SchemaVersion = 1,
            Game = game,
            Actions = all.Select((id, i) => new CatalogAction
            {
                Id = id,
                Name = id,
                Output = game is Game.Ets2 or Game.Ats
                    ? new ScsOutput { Input = "in-" + id }
                    : new VjoyOutput { Button = i + 1 },
            }).ToList(),
        };
    }

    public static GridCell Cell(int col = 0, int row = 0) => new() { Col = col, Row = row, ColSpan = 1, RowSpan = 1 };

    public static ToggleWidget Toggle(string id = "horn", string action = "horn") =>
        new() { Id = id, Cell = Cell(0, 0), Action = action, State = new StateBinding { Lit = "horn.lit" } };

    public static EncoderWidget Encoder(string id = "wiper") =>
        new() { Id = id, Cell = Cell(1, 0), Minus = "wiper-down", Plus = "wiper-up" };

    public static SwitchWidget Switch(string id = "lights", params string?[] actions) =>
        new()
        {
            Id = id,
            Cell = Cell(2, 0),
            Positions = (actions.Length > 0 ? actions : ["lights-off", "lights-park", "lights-low"])
                .Select((a, i) => new SwitchPosition { Angle = i * 30, Action = a }).ToList(),
            State = new StateBinding { Led = "lights.led" },
        };

    public static DualActionWidget Dual(string id = "engine", string? tap = "ign", string? hold = "starter",
        DualActionWidgetHoldMode mode = DualActionWidgetHoldMode.Fire) =>
        new() { Id = id, Cell = Cell(3, 0), Tap = tap, Hold = hold, HoldMode = mode, State = new StateBinding { Lit = "ign.lit", Led = "eng.led" } };

    public static Layout Layout(string template = Template, int columns = 4, int rows = 1, params Widget[] widgets) =>
        new()
        {
            SchemaVersion = 1,
            TemplateId = template,
            Grid = new Grid { Columns = columns, Rows = rows },
            Widgets = widgets.Length > 0 ? widgets : [Toggle(), Encoder(), Switch(), Dual()],
        };

    public static Profile Profile(Game game = Game.Ets2, params Layout[] layouts) =>
        new() { SchemaVersion = 1, Id = "p", Name = "p", Game = game, Layouts = layouts.Length > 0 ? layouts : [Layout()] };

    public static LayoutEngine NewEngine(Profile? profile = null, Catalog? catalog = null) =>
        new ProfileEngine(profile ?? Profile(), catalog ?? Catalog()).ForTemplate(Template)!;

    public static GestureMessage Msg(string widget, ButtonBox.Core.Contracts.Gesture gesture) => new() { WidgetId = widget, Gesture = gesture };

    public static SelectMessage Select(string widget, int position) => new() { WidgetId = widget, Position = position };
}
