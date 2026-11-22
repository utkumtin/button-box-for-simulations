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
