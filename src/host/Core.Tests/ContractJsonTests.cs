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

    // The schema allows only enum names. An integer would otherwise map silently to a gesture (3 = "plus").
    [Theory]
    [InlineData("""{"type":"gesture","widgetId":"a","gesture":3}""")]
    [InlineData("""{"type":"gesture","widgetId":"a","gesture":"3"}""")]
    [InlineData("""{"type":"gesture","widgetId":"a","gesture":"Tap"}""")]
    public void Enum_must_be_one_of_its_names(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ClientMessage>(json, ContractJson.Options));

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

    // Phase 3: the phone renders from these fields. A look that collapsed into one class, or a dropped
    // state binding, would make widgets render wrong or never light up.
    private const string Asset = "02abf8a01862694028bb6eed91cde12ab712b3e24337ef0905cbe4bcc539bff9";

    private static readonly string LooksLayoutJson = """
        {"schemaVersion":1,"templateId":"phone-a","grid":{"columns":4,"rows":2},"widgets":[
         {"type":"toggle","id":"t1","cell":{"col":0,"row":0,"colSpan":1,"rowSpan":1},"action":"beacon","look":{"mode":"icon","icon":"alarm-light"},"state":{"lit":"truck.light.beacon"}},
         {"type":"toggle","id":"t2","cell":{"col":1,"row":0,"colSpan":1,"rowSpan":1},"action":"beacon","look":{"mode":"images","image":"ASSET","on":"ASSET"}},
         {"type":"switch","id":"s1","cell":{"col":2,"row":0,"colSpan":2,"rowSpan":2},"positions":[{"label":"OFF","angle":-30,"action":"a"},{"icon":"car-light-dimmed","angle":30,"action":"b"}],
          "rotary":{"base":{"asset":"ASSET","scale":1},"knob":{"asset":"ASSET","scale":0.55,"offsetX":-0.01,"offsetY":0.02}}},
         {"type":"dualAction","id":"d1","cell":{"col":0,"row":1,"colSpan":2,"rowSpan":1},"tap":"on","hold":"start","holdMode":"fire","look":{"mode":"images","image":"ASSET"},"state":{"lit":"truck.electric.enabled","led":"isEngineRunning"}}]}
        """.Replace("ASSET", Asset, StringComparison.Ordinal);

    [Fact]
    public void Looks_rotary_and_state_bindings_round_trip()
    {
        var layout = RoundTrip<Layout>(LooksLayoutJson, typeof(Layout));
        var icon = Assert.IsType<IconLook>(((ToggleWidget)layout.Widgets[0]).Look);
        Assert.Equal("alarm-light", icon.Icon);
        Assert.Equal("truck.light.beacon", layout.Widgets[0].State?.Lit);
        var images = Assert.IsType<ImagesLook>(((ToggleWidget)layout.Widgets[1]).Look);
        Assert.Equal(Asset, images.On);
        var rotary = ((SwitchWidget)layout.Widgets[2]).Rotary;
        Assert.Equal(0.55, rotary?.Knob?.Scale);
        Assert.Null(rotary?.Highlight);
        Assert.Equal("car-light-dimmed", ((SwitchWidget)layout.Widgets[2]).Positions[1].Icon);
        var dual = (DualActionWidget)layout.Widgets[3];
        Assert.IsType<ImagesLook>(dual.Look);
        Assert.Equal("isEngineRunning", dual.State?.Led);
    }

    [Fact]
    public void No_layout_message_round_trips() =>
        RoundTrip<HostMessage>("""{"type":"noLayout"}""", typeof(NoLayoutMessage));

    [Theory]
    [InlineData("""{"mode":"rotary","icon":"x"}""")] // unknown look mode
    [InlineData("""{"icon":"x"}""")] // no mode
    [InlineData("""{"mode":"icon","icon":"x","image":"y"}""")] // field of another look
    public void Bad_look_is_rejected(string look)
    {
        var json = $$"""{"schemaVersion":1,"templateId":"a","grid":{"columns":1,"rows":1},"widgets":[{"type":"toggle","id":"w","cell":{"col":0,"row":0,"colSpan":1,"rowSpan":1},"action":"x","look":{{look}}}]}""";
        var ex = Record.Exception(() => JsonSerializer.Deserialize<Layout>(json, ContractJson.Options));
        Assert.True(ex is JsonException or NotSupportedException, $"expected a rejection, got {ex?.GetType().Name ?? "none"}");
    }

    [Fact]
    public void Unknown_field_in_a_rotary_layer_is_rejected()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Rotary>(
            $$$"""{"knob":{"asset":"{{{Asset}}}","rotation":5}}""", ContractJson.Options));

    // Phase 5 (decisions §5 "Widget behaviours"): the behaviour decides which engine rules run, so it must survive the round trip,
    // stay absent on old profiles, and only exist on the widget type it belongs to.
    private const string Cell = """
        "cell":{"col":0,"row":0,"colSpan":2,"rowSpan":2}
        """;

    [Fact]
    public void Widget_behaviours_round_trip_and_stay_optional()
    {
        var json = $$"""
            {"schemaVersion":1,"templateId":"a","grid":{"columns":6,"rows":2},"widgets":[
             {"type":"switch","id":"h",{{Cell}},"positions":[{"angle":-30,"action":"light-off"},{"angle":0,"action":"light-park"},{"angle":30,"action":"light-on"},{"angle":60,"action":"light-on"}],"behavior":"headlights"},
             {"type":"switch","id":"w",{{Cell}},"positions":[{"angle":-30,"action":"a"},{"angle":30,"action":"b"}],"behavior":"wipers"},
             {"type":"dualAction","id":"e",{{Cell}},"tap":"ignition-on","hold":"starter","holdMode":"fire","behavior":"engineStartStop"},
             {"type":"switch","id":"plain",{{Cell}},"positions":[{"angle":-30,"action":"a"},{"angle":30,"action":"b"}]}]}
            """;
        var layout = RoundTrip<Layout>(json, typeof(Layout));
        Assert.Equal(SwitchBehavior.Headlights, ((SwitchWidget)layout.Widgets[0]).Behavior);
        Assert.Equal(SwitchBehavior.Wipers, ((SwitchWidget)layout.Widgets[1]).Behavior);
        Assert.Equal(DualActionBehavior.EngineStartStop, ((DualActionWidget)layout.Widgets[2]).Behavior);
        Assert.Null(((SwitchWidget)layout.Widgets[3]).Behavior);
    }

    [Theory]
    [InlineData("""{"type":"switch","id":"s","CELL","positions":[{"angle":0,"action":"a"},{"angle":30,"action":"b"}],"behavior":"engineStartStop"}""")]
    [InlineData("""{"type":"dualAction","id":"d","CELL","tap":"a","holdMode":"fire","behavior":"headlights"}""")]
    [InlineData("""{"type":"toggle","id":"t","CELL","action":"a","behavior":"wipers"}""")]
    [InlineData("""{"type":"switch","id":"s","CELL","positions":[{"angle":0,"action":"a"},{"angle":30,"action":"b"}],"behavior":"Headlights"}""")]
    [InlineData("""{"type":"switch","id":"s","CELL","positions":[{"angle":0,"action":"a"},{"angle":30,"action":"b"}],"behavior":0}""")]
    public void Behaviour_of_another_widget_type_or_unknown_name_is_rejected(string widget)
    {
        var json = $$"""{"schemaVersion":1,"templateId":"a","grid":{"columns":6,"rows":2},"widgets":[{{widget.Replace("\"CELL\"", Cell, StringComparison.Ordinal)}}]}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Layout>(json, ContractJson.Options));
    }

    [Fact]
    public void Widget_without_type_is_rejected() =>
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<Widget>(
            """{"id":"w1","cell":{"col":0,"row":0,"colSpan":1,"rowSpan":1},"action":"a"}""", ContractJson.Options));
}
