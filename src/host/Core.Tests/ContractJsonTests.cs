using Xunit;
using System.Text.Json;
using System.Text.Json.Nodes;
using ButtonBox.Core.Contracts;

namespace ButtonBox.Core.Tests;

// The contract types are generated from /schemas. These tests pin the serializer behaviour the wire format
// depends on, so a generator or option change that silently alters it fails here.
public class ContractJsonTests
{
    private const string LayoutJson = """
        {"schemaVersion":1,"templateId":"phone-a","grid":{"columns":6,"rows":4},"widgets":[
         {"type":"toggle","id":"w1","cell":{"col":0,"row":0,"colSpan":1,"rowSpan":1},"label":"Lights","longPressGuard":true,"action":"headlights"},
         {"type":"encoder","id":"w2","cell":{"col":1,"row":0,"colSpan":2,"rowSpan":1},"minus":"tc-down","plus":"tc-up"},
         {"type":"switch","id":"w3","cell":{"col":3,"row":0,"colSpan":2,"rowSpan":2},"positions":[{"label":"A","angle":-45.5,"action":"map-a"},{"angle":45}],"cycle":"map-next"},
         {"type":"dualAction","id":"w4","cell":{"col":0,"row":1,"colSpan":1,"rowSpan":1},"tap":"engine-toggle","hold":"starter","holdMode":"starterTakeover"}]}
        """;

    private static readonly string Token = new('A', 43);

    private static T RoundTrip<T>(string json, Type expected)
    {
        var value = JsonSerializer.Deserialize<T>(json, ContractJson.Options);
        Assert.NotNull(value);
        Assert.Equal(expected, value.GetType());
        var back = JsonSerializer.Serialize(value, ContractJson.Options);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(back)), $"round trip changed the JSON: {back}");
        return value;
    }

    // Each message must come back as its own class and keep its "type": a union that collapses into
    // one class would let a client message be treated as another.
    [Fact]
    public void Client_messages_round_trip_to_their_own_class()
    {
        RoundTrip<ClientMessage>($$"""{"type":"hello","token":"{{Token}}"}""", typeof(HelloMessage));
        RoundTrip<ClientMessage>("""{"type":"gesture","widgetId":"btn-1","gesture":"previous"}""", typeof(GestureMessage));
        RoundTrip<ClientMessage>("""{"type":"select","widgetId":"sw-1","position":3}""", typeof(SelectMessage));
    }

    [Fact]
    public void Host_messages_round_trip_to_their_own_class()
    {
        RoundTrip<HostMessage>("""{"type":"welcome"}""", typeof(WelcomeMessage));
        RoundTrip<HostMessage>($$"""{"type":"layout","layout":{{LayoutJson}}}""", typeof(LayoutMessage));
        RoundTrip<HostMessage>("""{"type":"state","widgets":{"w1":{"lit":true},"w3":{"position":2,"led":"amber"}}}""", typeof(StateMessage));
        RoundTrip<HostMessage>("""{"type":"status","telemetry":true,"gameListening":false}""", typeof(StatusMessage));
    }

    // A browser may serialize "type" last. System.Text.Json rejects that unless the options allow it.
    [Fact]
    public void Discriminator_may_come_last()
    {
        var message = RoundTrip<ClientMessage>("""{"widgetId":"btn-1","gesture":"tap","type":"gesture"}""", typeof(GestureMessage));
        Assert.Equal("btn-1", ContractJson.WidgetId(message));
    }

    [Fact]
    public void Layout_keeps_every_widget_type()
    {
        var layout = RoundTrip<Layout>(LayoutJson, typeof(Layout));
        Assert.Equal(
            [typeof(ToggleWidget), typeof(EncoderWidget), typeof(SwitchWidget), typeof(DualActionWidget)],
            layout.Widgets.Select(w => w.GetType()));
        Assert.Equal(DualActionWidgetHoldMode.StarterTakeover, ((DualActionWidget)layout.Widgets[3]).HoldMode);
    }

    [Fact]
    public void Profile_and_catalog_round_trip()
    {
        RoundTrip<Profile>($$"""{"schemaVersion":1,"id":"p1","name":"My ETS2","game":"ets2","layouts":[{{LayoutJson}}]}""", typeof(Profile));
        var catalog = RoundTrip<Catalog>(
            """{"schemaVersion":1,"game":"acevo","actions":[{"id":"a","name":"A","output":{"kind":"vjoy","button":5},"stateField":"x"},{"id":"b","name":"B","output":{"kind":"scs","input":"light.high"}}]}""",
            typeof(Catalog));
        // The output of an action is a vjoy/scs union: it must not collapse into the first variant.
        Assert.IsType<VjoyOutput>(catalog.Actions[0].Output);
        Assert.IsType<ScsOutput>(catalog.Actions[1].Output);
    }

    // Security: until Phase 2 adds schema validation, these checks are the only guard against client input.
    [Fact]
    public void Unknown_message_type_is_rejected()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ClientMessage>("""{"type":"evil"}""", ContractJson.Options));

    [Fact]
    public void Unknown_field_is_rejected()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ClientMessage>(
            """{"type":"select","widgetId":"a","position":1,"extra":1}""", ContractJson.Options));

    [Fact]
    public void Unknown_field_in_a_widget_is_rejected()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Layout>(
            """{"schemaVersion":1,"templateId":"a","grid":{"columns":1,"rows":1},"widgets":[{"type":"toggle","id":"w","cell":{"col":0,"row":0,"colSpan":1,"rowSpan":1},"action":"x","extra":1}]}""",
            ContractJson.Options));

    [Fact]
    public void Missing_required_field_is_rejected()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ClientMessage>(
            """{"type":"gesture","widgetId":"a"}""", ContractJson.Options));

    // An absent optional property must not be written as null, which the schema does not allow.
    [Fact]
    public void Optional_fields_are_omitted_not_null()
    {
        var json = JsonSerializer.Serialize<HostMessage>(
            new StateMessage { Widgets = new Dictionary<string, WidgetState> { ["w1"] = new() { Lit = true } } }, ContractJson.Options);
        Assert.DoesNotContain("null", json);
    }

    // The wire value is "ac1", not the C# name "Ac1".
    [Fact]
    public void Enums_use_their_wire_values()
    {
        var json = JsonSerializer.Serialize(
            new Profile { SchemaVersion = 1, Id = "p", Name = "n", Game = Game.Ac1, Layouts = [] }, ContractJson.Options);
        Assert.Contains("\"game\":\"ac1\"", json);
        var profile = JsonSerializer.Deserialize<Profile>(
            """{"schemaVersion":1,"id":"p","name":"n","game":"acevo","layouts":[]}""", ContractJson.Options);
        Assert.Equal(Game.Acevo, profile?.Game);
    }

    // A message or widget without "type" must not become a bare base object: the host would otherwise
    // accept a message it cannot map to any widget action. The bases are abstract, so System.Text.Json throws
    // NotSupportedException here (not JsonException); the WebSocket handler must treat both as a bad message.
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"widgetId":"btn-1","gesture":"tap"}""")]
    public void Client_message_without_type_is_rejected(string json) =>
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ClientMessage>(json, ContractJson.Options));

    [Fact]
    public void Widget_without_type_is_rejected() =>
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<Widget>(
            """{"id":"w1","cell":{"col":0,"row":0,"colSpan":1,"rowSpan":1},"action":"a"}""", ContractJson.Options));
}
