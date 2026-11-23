using System.Text;
using System.Text.Json;
using ButtonBox.Core.Contracts;
using Xunit;

namespace ButtonBox.Security.Tests;

// Why: saved and imported profiles are untrusted. ContractJson.Options rejects unknown fields, missing required fields
// and unknown enum names, but not null for required references and not any bound or pattern. These tests hold the
// hand-written checks to the schemas (decisions §9) so a hostile profile can never reach the phone or the engine.
public class ProfileValidatorTests
{
    private const string Asset = "02abf8a01862694028bb6eed91cde12ab712b3e24337ef0905cbe4bcc539bff9";

    private sealed class UnknownWidget : Widget;

    private sealed class UnknownLook : Look;

    private static GridCell Cell(int col = 0) => new() { Col = col, Row = 0, ColSpan = 1, RowSpan = 1 };

    // One valid profile with every widget and look kind; each test breaks exactly one field.
    private static Profile Valid() => new()
    {
        SchemaVersion = 1,
        Id = "demo",
        Name = "Simulator demo",
        Game = Game.Ets2,
        Layouts =
        [
            new Layout
            {
                SchemaVersion = 1,
                TemplateId = "iphone-14-pro",
                Grid = new Grid { Columns = 8, Rows = 3 },
                Widgets =
                [
                    new ToggleWidget
                    {
                        Id = "beacon",
                        Cell = Cell(0),
                        Label = "BEACON",
                        State = new StateBinding { Lit = "truck.light.beacon", Led = "isEngineRunning" },
                        Action = "beacon-toggle",
                        Look = new IconLook { Icon = "car-light-high" },
                    },
                    new EncoderWidget { Id = "volume", Cell = Cell(1), Minus = "vol-down", Plus = "vol-up" },
                    new SwitchWidget
                    {
                        Id = "lights",
                        Cell = Cell(2),
                        Cycle = "light-cycle",
                        Positions =
                        [
                            new SwitchPosition { Label = "OFF", Angle = -30, Action = "light-off" },
                            new SwitchPosition { Angle = 30, Action = "light-on", Icon = "car-light-dimmed" },
                        ],
                        Rotary = new Rotary
                        {
                            Base = new RotaryLayer { Asset = Asset },
                            Knob = new RotaryLayer { Asset = Asset, Scale = 1, OffsetX = 0, OffsetY = 0 },
                        },
                    },
                    new DualActionWidget
                    {
                        Id = "engine",
                        Cell = Cell(3),
                        Tap = "ignition-on",
                        Hold = "starter",
                        HoldMode = DualActionWidgetHoldMode.Fire,
                        Look = new ImagesLook { Image = Asset, On = Asset },
                    },
                ],
            },
        ],
    };

    private static Layout Layout0(Profile p) => (Layout)p.Layouts[0];

    private static T W<T>(Profile p, int index) where T : Widget => (T)Layout0(p).Widgets[index];

    private static ToggleWidget Toggle(Profile p) => W<ToggleWidget>(p, 0);

    private static EncoderWidget Encoder(Profile p) => W<EncoderWidget>(p, 1);

    private static SwitchWidget Switch(Profile p) => W<SwitchWidget>(p, 2);

    private static DualActionWidget Dual(Profile p) => W<DualActionWidget>(p, 3);

    private static string Name(int codePoints) => string.Concat(Enumerable.Repeat("😀", codePoints));

    private static IReadOnlyList<SwitchPosition> SwitchPositions(int count) =>
        Enumerable.Range(0, count).Select(i => new SwitchPosition { Angle = i }).ToArray();

    // ---- valid ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_demo_profile_is_valid()
    {
        var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(Find("src", "host", "App", "Demo", "demo.profile.json")), ContractJson.Options);
        Assert.Empty(ProfileValidator.Problems(profile));
    }

    [Fact]
    public void The_test_profile_is_valid()
    {
        Assert.Empty(ProfileValidator.Problems(Valid()));
    }

    // Why: each limit is inclusive; a validator that is off by one rejects legal profiles or accepts oversized ones.
    [Fact]
    public void Every_limit_is_inclusive_at_its_boundary()
    {
        var profile = Valid();
        Layout0(profile).Grid.Columns = 24;
        Layout0(profile).Grid.Rows = 16;
        Toggle(profile).Cell = new GridCell { Col = 64, Row = 64, ColSpan = 64, RowSpan = 64 };
        Toggle(profile).Label = new string('x', 32);
        Toggle(profile).State = new StateBinding { Lit = "a" + new string('b', 63) };
        Switch(profile).Positions = SwitchPositions(8);
        Switch(profile).Positions[0].Angle = 180;
        Switch(profile).Positions[1].Angle = -180;
        Switch(profile).Rotary = new Rotary
        {
            Base = new RotaryLayer { Asset = Asset, Scale = 0.1, OffsetX = -0.5, OffsetY = 0.5 },
            Knob = new RotaryLayer { Asset = Asset, Scale = 2, OffsetX = 0.5, OffsetY = -0.5 },
        };
        Assert.Empty(ProfileValidator.Problems(profile));
    }

    // Why: the limit counts code points like the schema's maxLength, not UTF-16 units; 64 emoji are 128 units.
    [Fact]
    public void Name_limit_counts_code_points()
    {
        var profile = Valid();
        profile.Name = Name(64);
        Assert.Equal(128, profile.Name.Length);
        Assert.Empty(ProfileValidator.Problems(profile));

        profile.Name = Name(65);
        Assert.NotEmpty(ProfileValidator.Problems(profile));
    }

    [Fact]
    public void Sixteen_layouts_and_256_widgets_are_allowed()
    {
        var profile = Valid();
        var layout = Layout0(profile);
        layout.Widgets = Enumerable.Range(0, 256).Select(i => (Widget)new EncoderWidget { Id = "w" + i, Cell = Cell(), Minus = "a", Plus = "b" }).ToArray();
        profile.Layouts = Enumerable.Range(0, 16).Select(_ => layout).ToArray();
        Assert.Empty(ProfileValidator.Problems(profile));
    }

    // Why: U+202E is not a control character in the schema's pattern (only U+0000-U+001F and U+007F are). The label is
    // drawn as text by the phone, so a bidi override cannot run anything; rejecting it would be stricter than the contract.
    [Fact]
    public void A_right_to_left_override_in_a_label_is_accepted()
    {
        var profile = Valid();
        Toggle(profile).Label = "A‮B";
        Assert.Empty(ProfileValidator.Problems(profile));
    }

    // ---- one broken field per rule --------------------------------------------------------------------------------

    private static readonly (string Name, Action<Profile> Break, string Expect)[] BrokenFields =
    [
        ("profile schemaVersion 2", p => p.SchemaVersion = 2, "Profile: schemaVersion"),
        ("profile id uppercase", p => p.Id = "Demo", "Profile: the id"),
        ("profile id null", p => p.Id = null!, "Profile: the id"),
        ("profile id with trailing newline", p => p.Id = "demo\n", "Profile: the id"),
        ("profile name null", p => p.Name = null!, "Profile: the name"),
        ("profile name empty", p => p.Name = "", "Profile: the name"),
        ("profile name 65 code points", p => p.Name = Name(65), "Profile: the name"),
        ("profile name with newline", p => p.Name = "a\nb", "Profile: the name"),
        ("profile name with DEL", p => p.Name = "a\u007Fb", "Profile: the name"),
        ("profile name with lone surrogate", p => p.Name = "a\uD800b", "Profile: the name"),
        ("profile game undefined", p => p.Game = (Game)99, "Profile: the game"),
        ("profile layouts null", p => p.Layouts = null!, "layouts are missing"),
        ("17 layouts", p => p.Layouts = Enumerable.Range(0, 17).Select(_ => Layout0(p)).ToArray(), "at most 16 layouts"),
        ("layout null", p => p.Layouts = [null!], "Layout 1 is missing"),
        ("layout schemaVersion 2", p => Layout0(p).SchemaVersion = 2, "Layout 'iphone-14-pro': schemaVersion"),
        ("layout templateId uppercase", p => Layout0(p).TemplateId = "IPHONE", "templateId"),
        ("layout templateId null", p => Layout0(p).TemplateId = null!, "templateId"),
        ("grid null", p => Layout0(p).Grid = null!, "grid is missing"),
        ("grid 25 columns", p => Layout0(p).Grid.Columns = 25, "1 to 24 columns"),
        ("grid 0 columns", p => Layout0(p).Grid.Columns = 0, "1 to 24 columns"),
        ("grid 17 rows", p => Layout0(p).Grid.Rows = 17, "1 to 16 rows"),
        ("grid 0 rows", p => Layout0(p).Grid.Rows = 0, "1 to 16 rows"),
        ("widgets null", p => Layout0(p).Widgets = null!, "widgets are missing"),
        ("257 widgets", p => Layout0(p).Widgets = Enumerable.Range(0, 257).Select(i => (Widget)new EncoderWidget { Id = "w" + i, Cell = Cell(), Minus = "a", Plus = "b" }).ToArray(), "at most 256 widgets"),
        ("widget null", p => Layout0(p).Widgets = [null!], "widget 1 is missing"),
        ("widget id uppercase", p => Toggle(p).Id = "Beacon", "widget 1: the id"),
        ("widget id null", p => Toggle(p).Id = null!, "widget 1: the id"),
        ("unknown widget subclass", p => Layout0(p).Widgets = [new UnknownWidget { Id = "x", Cell = Cell() }], "widget type is not known"),
        ("cell null", p => Toggle(p).Cell = null!, "cell is missing"),
        ("cell col 65", p => Toggle(p).Cell = new GridCell { Col = 65, Row = 0, ColSpan = 1, RowSpan = 1 }, "cell position"),
        ("cell row -1", p => Toggle(p).Cell = new GridCell { Col = 0, Row = -1, ColSpan = 1, RowSpan = 1 }, "cell position"),
        ("cell colSpan 0", p => Toggle(p).Cell = new GridCell { Col = 0, Row = 0, ColSpan = 0, RowSpan = 1 }, "cell span"),
        ("cell rowSpan 65", p => Toggle(p).Cell = new GridCell { Col = 0, Row = 0, ColSpan = 1, RowSpan = 65 }, "cell span"),
        ("label empty", p => Toggle(p).Label = "", "'beacon': the label"),
        ("label with newline", p => Toggle(p).Label = "a\nb", "'beacon': the label"),
        ("label with NUL", p => Toggle(p).Label = "a\0b", "'beacon': the label"),
        ("label 33 characters", p => Toggle(p).Label = new string('x', 33), "'beacon': the label"),
        ("state.lit starts with a digit", p => Toggle(p).State = new StateBinding { Lit = "1abc" }, "state.lit"),
        ("state.led with a space", p => Toggle(p).State = new StateBinding { Led = "a b" }, "state.led"),
        ("state.lit 65 characters", p => Toggle(p).State = new StateBinding { Lit = "a" + new string('b', 64) }, "state.lit"),
        ("toggle action null", p => Toggle(p).Action = null!, "action is missing"),
        ("toggle action path", p => Toggle(p).Action = "../../x", "action is missing or not a valid id"),
        ("icon look null icon", p => Toggle(p).Look = new IconLook { Icon = null! }, "look.icon"),
        ("icon look uppercase", p => Toggle(p).Look = new IconLook { Icon = "Car" }, "look.icon"),
        ("unknown look subclass", p => Toggle(p).Look = new UnknownLook(), "look mode is not known"),
        ("images look uppercase asset", p => Dual(p).Look = new ImagesLook { Image = Asset.ToUpperInvariant() }, "look.image"),
        ("images look url", p => Dual(p).Look = new ImagesLook { Image = "https://evil.example/x.png" }, "look.image"),
        ("images look null image", p => Dual(p).Look = new ImagesLook { Image = null! }, "look.image"),
        ("images look bad on", p => Dual(p).Look = new ImagesLook { Image = Asset, On = "abc" }, "look.on"),
        ("encoder minus null", p => Encoder(p).Minus = null!, "minus"),
        ("encoder plus uppercase", p => Encoder(p).Plus = "Up", "plus"),
        ("switch positions null", p => Switch(p).Positions = null!, "positions are missing"),
        ("switch 1 position", p => Switch(p).Positions = SwitchPositions(1), "2 to 8 positions"),
        ("switch 9 positions", p => Switch(p).Positions = SwitchPositions(9), "2 to 8 positions"),
        ("switch position null", p => Switch(p).Positions = [null!, new SwitchPosition { Angle = 0 }], "position 1 is missing"),
        ("angle 181", p => Switch(p).Positions[0].Angle = 181, "angle"),
        ("angle -181", p => Switch(p).Positions[0].Angle = -181, "angle"),
        ("angle NaN", p => Switch(p).Positions[0].Angle = double.NaN, "angle"),
        ("angle infinity", p => Switch(p).Positions[0].Angle = double.PositiveInfinity, "angle"),
        ("position label empty", p => Switch(p).Positions[0].Label = "", "position 1: the label"),
        ("position action uppercase", p => Switch(p).Positions[0].Action = "A", "position 1: action"),
        ("position icon path", p => Switch(p).Positions[0].Icon = "../x", "position 1: icon"),
        ("switch cycle uppercase", p => Switch(p).Cycle = "Cycle", "cycle"),
        ("rotary asset uppercase", p => Switch(p).Rotary!.Base = new RotaryLayer { Asset = Asset.ToUpperInvariant() }, "rotary.base.asset"),
        ("rotary asset null", p => Switch(p).Rotary!.Knob = new RotaryLayer { Asset = null! }, "rotary.knob.asset"),
        ("rotary asset 63 characters", p => Switch(p).Rotary!.Highlight = new RotaryLayer { Asset = Asset[..63] }, "rotary.highlight.asset"),
        ("scale 2.01", p => Switch(p).Rotary!.Knob = new RotaryLayer { Asset = Asset, Scale = 2.01 }, "scale"),
        ("scale 0.09", p => Switch(p).Rotary!.Knob = new RotaryLayer { Asset = Asset, Scale = 0.09 }, "scale"),
        ("scale NaN", p => Switch(p).Rotary!.Knob = new RotaryLayer { Asset = Asset, Scale = double.NaN }, "scale"),
        ("offsetX 0.51", p => Switch(p).Rotary!.Knob = new RotaryLayer { Asset = Asset, OffsetX = 0.51 }, "offsetX"),
        ("offsetY -0.51", p => Switch(p).Rotary!.Knob = new RotaryLayer { Asset = Asset, OffsetY = -0.51 }, "offsetY"),
        ("dual tap uppercase", p => Dual(p).Tap = "Tap", "tap"),
        ("dual hold path", p => Dual(p).Hold = "a/b", "hold"),
        ("dual holdMode undefined", p => Dual(p).HoldMode = (DualActionWidgetHoldMode)7, "holdMode"),
    ];

    [Fact]
    public void Each_rule_reports_its_own_problem()
    {
        Assert.Empty(ProfileValidator.Problems(Valid()));
        var failures = new List<string>();
        foreach (var (name, brk, expect) in BrokenFields)
        {
            var profile = Valid();
            brk(profile);
            var problems = ProfileValidator.Problems(profile);
            if (!problems.Any(p => p.Contains(expect, StringComparison.Ordinal)))
            {
                failures.Add($"{name}: expected a problem containing '{expect}', got [{string.Join(" | ", problems)}]");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void A_null_profile_is_a_problem_not_an_exception()
    {
        Assert.Single(ProfileValidator.Problems(null));
    }

    // Why: the problems are shown in the editor; a hostile id must be named by position, never echoed back.
    [Fact]
    public void An_invalid_id_is_never_quoted_in_a_problem()
    {
        var profile = Valid();
        Toggle(profile).Id = "<img src=x onerror=alert(1)>";
        Layout0(profile).TemplateId = "<b>evil</b>";
        var all = string.Join("\n", ProfileValidator.Problems(profile));
        Assert.DoesNotContain("evil", all);
        Assert.DoesNotContain("onerror", all);
        Assert.Contains("widget 1", all);
        Assert.Contains("Layout 1", all);
    }

    // Why: a hostile profile must not make the editor render thousands of lines or the host build megabyte strings.
    [Fact]
    public void Problems_are_capped_at_100_and_300_characters()
    {
        var profile = Valid();
        Layout0(profile).Widgets = Enumerable.Range(0, 256)
            .Select(i => (Widget)new ToggleWidget { Id = "BAD", Cell = null!, Label = new string('\n', 5000), Action = null! })
            .ToArray();
        var problems = ProfileValidator.Problems(profile);
        Assert.Equal(100, problems.Count);
        Assert.All(problems, p => Assert.True(p.Length <= 300));
    }

    // ---- malicious profile corpus (Gate 4) -----------------------------------------------------------------------

    private const string CellJson = "{\"col\":0,\"row\":0,\"colSpan\":1,\"rowSpan\":1}";

    private static string Toggle(string extra = "", string id = "a", string action = "\"x\"") =>
        $"{{\"type\":\"toggle\",\"id\":\"{id}\",\"cell\":{CellJson},\"action\":{action}{extra}}}";

    private static string Doc(string name = "N", string widgets = "", string root = "", string game = "\"ets2\"", string layouts = "") =>
        "{\"schemaVersion\":1,\"id\":\"p\",\"name\":\"" + name + "\",\"game\":" + game + root + ",\"layouts\":"
        + (layouts.Length > 0 ? layouts : "[{\"schemaVersion\":1,\"templateId\":\"t\",\"grid\":{\"columns\":8,\"rows\":3},\"widgets\":[" + (widgets.Length > 0 ? widgets : Toggle()) + "]}]")
        + "}";

    public static TheoryData<string, string> Hostile()
    {
        var data = new TheoryData<string, string>
        {
            { "unknown field onClick", Doc(widgets: Toggle(",\"onClick\":\"alert(1)\"")) },
            { "unknown root field __proto__", Doc(root: ",\"__proto__\":{\"polluted\":true}") },
            { "unknown widget field __proto__", Doc(widgets: Toggle(",\"__proto__\":{}")) },
            { "numeric enum game", Doc(game: "0") },
            { "numeric holdMode", Doc(widgets: "{\"type\":\"dualAction\",\"id\":\"a\",\"cell\":" + CellJson + ",\"holdMode\":1}") },
            { "unknown widget type", Doc(widgets: "{\"type\":\"script\",\"id\":\"a\",\"cell\":" + CellJson + "}") },
            { "string where a number is expected", Doc(widgets: "{\"type\":\"toggle\",\"id\":\"a\",\"cell\":{\"col\":\"0\",\"row\":0,\"colSpan\":1,\"rowSpan\":1},\"action\":\"x\"}") },
            { "number where a string is expected", Doc(name: "N", widgets: Toggle(action: "5")) },
            { "action null", Doc(widgets: Toggle(action: "null")) },
            { "missing required field", Doc(widgets: "{\"type\":\"toggle\",\"id\":\"a\",\"cell\":" + CellJson + "}") },
            { "deeply nested arrays", Doc(layouts: new string('[', 64) + new string(']', 64)) },
            { "nested arrays beyond the serializer depth", Doc(layouts: new string('[', 200) + new string(']', 200)) },
            { "action path traversal", Doc(widgets: Toggle(action: "\"../../x\"")) },
            { "image url", Doc(widgets: Toggle(",\"look\":{\"mode\":\"images\",\"image\":\"https://evil/x.png\"}")) },
            { "label with NUL", Doc(widgets: Toggle(",\"label\":\"a\\u0000b\"")) },
            { "label with newline", Doc(widgets: Toggle(",\"label\":\"a\\nb\"")) },
            { "widget id uppercase", Doc(widgets: Toggle(id: "ABC")) },
            { "10000 character name", Doc(name: new string('n', 10_000)) },
            { "name with NUL", Doc(name: "a\\u0000b") },
            { "grid 99 x 99", Doc(layouts: "[{\"schemaVersion\":1,\"templateId\":\"t\",\"grid\":{\"columns\":99,\"rows\":99},\"widgets\":[]}]") },
            { "huge numbers", Doc(layouts: "[{\"schemaVersion\":1,\"templateId\":\"t\",\"grid\":{\"columns\":1e999,\"rows\":3},\"widgets\":[]}]") },
        };
        return data;
    }

    // Why: whatever the bytes are, the outcome must be a JsonException or a non-empty problem list; any other exception
    // type would be an unhandled error in the profile store.
    [Theory]
    [MemberData(nameof(Hostile))]
    public void Hostile_json_ends_in_a_json_error_or_a_problem(string name, string json)
    {
        _ = name;
        Profile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<Profile>(json, ContractJson.Options);
        }
        catch (JsonException)
        {
            return;
        }

        Assert.NotEmpty(ProfileValidator.Problems(profile));
    }

    [Fact]
    public void The_hostile_corpus_baseline_is_valid_json_for_a_valid_profile()
    {
        // Why: guards the corpus itself; if the template is wrong every hostile case would pass for the wrong reason.
        var profile = JsonSerializer.Deserialize<Profile>(Doc(), ContractJson.Options);
        Assert.Empty(ProfileValidator.Problems(profile));
    }

    [Fact]
    public void A_right_to_left_override_in_json_is_accepted()
    {
        var profile = JsonSerializer.Deserialize<Profile>(Doc(widgets: Toggle(",\"label\":\"a\\u202Eb\"")), ContractJson.Options);
        Assert.Empty(ProfileValidator.Problems(profile));
    }

    private static string Find(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine([dir.FullName, .. relative]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join('/', relative) + " not found above " + AppContext.BaseDirectory);
    }
}
