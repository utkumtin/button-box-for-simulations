using Xunit;
using System.Reflection;
using System.Text.Json;
using ButtonBox.Security;

namespace ButtonBox.Security.Tests;

// Why: the validator's limits are hand-written copies of /schemas. If someone edits a schema and not the validator
// (or vice versa), the phone-facing checks silently stop matching the documented contract. These tests make that loud.
public class SchemaDriftTests
{
    private static string SchemasDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "schemas", "protocol.schema.json")))
            {
                return System.IO.Path.Combine(dir.FullName, "schemas");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("schemas/protocol.schema.json not found above " + AppContext.BaseDirectory);
    }

    // Anchors differ by design: schemas use ^…$, the validator uses \A…\z.
    private static string Normalize(string schemaPattern)
    {
        Assert.StartsWith("^", schemaPattern);
        Assert.EndsWith("$", schemaPattern);
        return @"\A" + schemaPattern[1..^1] + @"\z";
    }

    private static JsonElement Node(JsonElement root, params object[] steps)
    {
        var current = root;
        foreach (var step in steps)
        {
            current = step is int i ? current[i] : current.GetProperty((string)step);
        }

        return current;
    }

    private static List<string> Compare(Func<string, string> transform)
    {
        var problems = new List<string>();
        JsonDocument Load(string name) => JsonDocument.Parse(transform(File.ReadAllText(System.IO.Path.Combine(SchemasDirectory(), name))));

        void Eq<T>(string what, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                problems.Add($"{what}: validator has {expected}, schema has {actual}");
            }
        }

        void Seq(string what, IEnumerable<string> expected, JsonElement array)
        {
            var actual = array.EnumerateArray().Select(e => e.GetString()!).ToArray();
            if (!expected.SequenceEqual(actual))
            {
                problems.Add($"{what}: validator has [{string.Join(",", expected)}], schema has [{string.Join(",", actual)}]");
            }
        }

        void Range(string what, JsonElement prop, double min, double max)
        {
            Eq(what + ".minimum", min, prop.GetProperty("minimum").GetDouble());
            Eq(what + ".maximum", max, prop.GetProperty("maximum").GetDouble());
        }

        using var protocol = Load("protocol.schema.json");
        using var common = Load("common.schema.json");
        using var pairing = Load("pairing.schema.json");
        using var editor = Load("editor-api.schema.json");
        using var template = Load("template.schema.json");
        using var profile = Load("profile.schema.json");
        using var layout = Load("layout.schema.json");

        var token = Node(protocol.RootElement, "definitions", "helloMessage", "allOf", 1, "properties", "token");
        Eq("token.pattern", ContractValidator.TokenPattern, Normalize(token.GetProperty("pattern").GetString()!));
        Eq("token.maxLength", ContractValidator.TokenLength, token.GetProperty("maxLength").GetInt32());

        Seq("gesture.enum", ContractValidator.GestureNames, Node(protocol.RootElement, "definitions", "gestureMessage", "allOf", 1, "properties", "gesture", "enum"));
        Range("position", Node(protocol.RootElement, "definitions", "selectMessage", "allOf", 1, "properties", "position"), ContractValidator.PositionMin, ContractValidator.PositionMax);

        var id = Node(common.RootElement, "definitions", "id");
        Eq("id.pattern", ContractValidator.IdPattern, Normalize(id.GetProperty("pattern").GetString()!));
        Eq("id.maxLength", ContractValidator.IdMaxLength, id.GetProperty("maxLength").GetInt32());

        var screen = Node(common.RootElement, "definitions", "screenInfo", "properties");
        Range("screen.width", screen.GetProperty("width"), ContractValidator.ScreenSizeMin, ContractValidator.ScreenSizeMax);
        Range("screen.height", screen.GetProperty("height"), ContractValidator.ScreenSizeMin, ContractValidator.ScreenSizeMax);
        Range("screen.pixelRatio", screen.GetProperty("pixelRatio"), ContractValidator.PixelRatioMin, ContractValidator.PixelRatioMax);
        foreach (var side in new[] { "top", "right", "bottom", "left" })
        {
            Range("safeArea." + side, Node(common.RootElement, "definitions", "safeArea", "properties", side), ContractValidator.SafeAreaMin, ContractValidator.SafeAreaMax);
        }

        var code = Node(pairing.RootElement, "definitions", "pairRequest", "properties", "code");
        Eq("code.pattern", ContractValidator.CodePattern, Normalize(code.GetProperty("pattern").GetString()!));
        Eq("code.maxLength", ContractValidator.CodeLength, code.GetProperty("maxLength").GetInt32());

        var name = Node(editor.RootElement, "definitions", "deviceName");
        Eq("deviceName.pattern", ContractValidator.DeviceNamePattern, Normalize(name.GetProperty("pattern").GetString()!));
        Eq("deviceName.minLength", ContractValidator.DeviceNameMinLength, name.GetProperty("minLength").GetInt32());
        Eq("deviceName.maxLength", ContractValidator.DeviceNameMaxLength, name.GetProperty("maxLength").GetInt32());

        var props = Node(template.RootElement, "properties");
        Eq("template.schemaVersion", ContractValidator.TemplateSchemaVersion, props.GetProperty("schemaVersion").GetProperty("const").GetInt32());
        Eq("template.name.minLength", ContractValidator.TemplateNameMinLength, props.GetProperty("name").GetProperty("minLength").GetInt32());
        Eq("template.name.maxLength", ContractValidator.TemplateNameMaxLength, props.GetProperty("name").GetProperty("maxLength").GetInt32());
        Seq("cutout.enum", ContractValidator.CutoutNames, props.GetProperty("cutout").GetProperty("enum"));

        void Pattern(string what, string expectedPattern, JsonElement def, int? minLength, int maxLength)
        {
            Eq(what + ".pattern", expectedPattern, Normalize(def.GetProperty("pattern").GetString()!));
            Eq(what + ".maxLength", maxLength, def.GetProperty("maxLength").GetInt32());
            if (minLength is { } min)
            {
                Eq(what + ".minLength", min, def.GetProperty("minLength").GetInt32());
            }
        }

        // Profile and layout (ProfileValidator).
        Eq("profile.schemaVersion", ProfileValidator.SchemaVersion, Node(profile.RootElement, "properties", "schemaVersion").GetProperty("const").GetInt32());
        Eq("layout.schemaVersion", ProfileValidator.SchemaVersion, Node(layout.RootElement, "properties", "schemaVersion").GetProperty("const").GetInt32());
        Eq("profile.layouts.maxItems", ProfileValidator.MaxLayouts, Node(profile.RootElement, "properties", "layouts").GetProperty("maxItems").GetInt32());
        Eq("layout.widgets.maxItems", ProfileValidator.MaxWidgets, Node(layout.RootElement, "properties", "widgets").GetProperty("maxItems").GetInt32());
        Range("grid.columns", Node(layout.RootElement, "definitions", "grid", "properties", "columns"), ProfileValidator.GridColumnsMin, ProfileValidator.GridColumnsMax);
        Range("grid.rows", Node(layout.RootElement, "definitions", "grid", "properties", "rows"), ProfileValidator.GridRowsMin, ProfileValidator.GridRowsMax);

        var cell = Node(common.RootElement, "definitions", "gridCell", "properties");
        Range("cell.col", cell.GetProperty("col"), ProfileValidator.CellPositionMin, ProfileValidator.CellPositionMax);
        Range("cell.row", cell.GetProperty("row"), ProfileValidator.CellPositionMin, ProfileValidator.CellPositionMax);
        Range("cell.colSpan", cell.GetProperty("colSpan"), ProfileValidator.CellSpanMin, ProfileValidator.CellSpanMax);
        Range("cell.rowSpan", cell.GetProperty("rowSpan"), ProfileValidator.CellSpanMin, ProfileValidator.CellSpanMax);

        Pattern("assetId", ProfileValidator.AssetIdPattern, Node(common.RootElement, "definitions", "assetId"), null, ProfileValidator.AssetIdLength);
        Pattern("telemetryField", ProfileValidator.TelemetryFieldPattern, Node(common.RootElement, "definitions", "telemetryField"), null, ProfileValidator.TelemetryFieldMaxLength);
        Pattern("label", ProfileValidator.LabelPattern, Node(common.RootElement, "definitions", "label"), ProfileValidator.LabelMinLength, ProfileValidator.LabelMaxLength);
        Pattern("profileName", ProfileValidator.ProfileNamePattern, Node(common.RootElement, "definitions", "profileName"), ProfileValidator.ProfileNameMinLength, ProfileValidator.ProfileNameMaxLength);

        var positions = Node(layout.RootElement, "definitions", "switchWidget", "allOf", 1, "properties", "positions");
        Eq("positions.minItems", ProfileValidator.PositionsMin, positions.GetProperty("minItems").GetInt32());
        Eq("positions.maxItems", ProfileValidator.PositionsMax, positions.GetProperty("maxItems").GetInt32());
        Range("angle", Node(layout.RootElement, "definitions", "switchPosition", "properties", "angle"), ProfileValidator.AngleMin, ProfileValidator.AngleMax);
        Range("rotary.scale", Node(layout.RootElement, "definitions", "rotaryLayer", "properties", "scale"), ProfileValidator.ScaleMin, ProfileValidator.ScaleMax);
        Range("rotary.offsetX", Node(layout.RootElement, "definitions", "rotaryLayer", "properties", "offsetX"), ProfileValidator.OffsetMin, ProfileValidator.OffsetMax);
        Range("rotary.offsetY", Node(layout.RootElement, "definitions", "rotaryLayer", "properties", "offsetY"), ProfileValidator.OffsetMin, ProfileValidator.OffsetMax);
        Seq("holdMode.enum", ProfileValidator.HoldModeNames, Node(layout.RootElement, "definitions", "dualActionWidget", "allOf", 1, "properties", "holdMode", "enum"));

        // ImageCheck limits (the import limits the editor API documents for assets).
        var asset = Node(editor.RootElement, "definitions", "assetInfo", "properties");
        Range("asset.size", asset.GetProperty("size"), ImageCheck.MinSize, ImageCheck.MaxSize);
        Eq("asset.bytes.maximum", ImageCheck.MaxBytes, asset.GetProperty("bytes").GetProperty("maximum").GetInt32());

        return problems;
    }

    [Fact]
    public void Validator_limits_equal_the_schemas()
    {
        Assert.Empty(Compare(text => text));
    }

    // Why: proves the drift test can fail - it must notice a changed limit in the schema file.
    [Theory]
    [InlineData("\"maximum\": 7", "\"maximum\": 8")]
    [InlineData("{43}$\"", "{44}$\"")]
    [InlineData("\"previous\"", "\"back\"")]
    [InlineData("\"maxLength\": 43", "\"maxLength\": 44")]
    [InlineData("\"pixelRatio\": { \"type\": \"number\", \"minimum\": 1,", "\"pixelRatio\": { \"type\": \"number\", \"minimum\": 2,")]
    [InlineData("\"^[0-9]{6}$\"", "\"^[0-9]{7}$\"")]
    [InlineData("\"maxLength\": 64,\n      \"pattern\"", "\"maxLength\": 65,\n      \"pattern\"")]
    [InlineData("\"const\": 1", "\"const\": 2")]
    [InlineData("\"dynamicIsland\"", "\"island\"")]
    [InlineData("\"maxItems\": 16,", "\"maxItems\": 17,")]
    [InlineData("\"maximum\": 24", "\"maximum\": 25")]
    [InlineData("\"maximum\": 16", "\"maximum\": 17")]
    [InlineData("\"minItems\": 2", "\"minItems\": 3")]
    [InlineData("\"maxItems\": 8", "\"maxItems\": 9")]
    [InlineData("\"minimum\": -180", "\"minimum\": -179")]
    [InlineData("\"minimum\": 0.1", "\"minimum\": 0.2")]
    [InlineData("\"minimum\": -0.5", "\"minimum\": -0.4")]
    [InlineData("\"fire\"", "\"fired\"")]
    [InlineData("[0-9a-f]{64}", "[0-9a-f]{63}")]
    [InlineData("{0,63}$", "{0,62}$")]
    [InlineData("\"maxLength\": 32,", "\"maxLength\": 33,")]
    [InlineData("\\\\u001f\\\\u007f]+$", "\\\\u001e\\\\u007f]+$")]
    [InlineData("\"colSpan\": { \"type\": \"integer\", \"minimum\": 1, \"maximum\": 64 }", "\"colSpan\": { \"type\": \"integer\", \"minimum\": 1, \"maximum\": 63 }")]
    [InlineData("\"col\": { \"type\": \"integer\", \"minimum\": 0, \"maximum\": 64 }", "\"col\": { \"type\": \"integer\", \"minimum\": 0, \"maximum\": 65 }")]
    [InlineData("\"minimum\": 512", "\"minimum\": 513")]
    [InlineData("\"maximum\": 2048", "\"maximum\": 4096")]
    [InlineData("4194304", "4194305")]
    public void A_changed_schema_value_is_detected(string from, string to)
    {
        var touched = false;
        var problems = Compare(text =>
        {
            var changed = text.Replace(from, to, StringComparison.Ordinal);
            touched |= changed != text;
            return changed;
        });

        Assert.True(touched, "the mutation did not match any schema text; update this test");
        Assert.NotEmpty(problems);
    }
}
