using Xunit;
using System.Text.Json;
using System.Text.Json.Nodes;
using ButtonBox.Core.Contracts;

namespace ButtonBox.Core.Tests;

// The Phase 4 editor API contracts. The editor UI and the host routes are written against them in parallel, so their wire
// format is pinned here: absent optionals stay absent, enums are names, and anything unexpected is refused.
public class EditorContractsTests
{
    private const string Hash = "02abf8a01862694028bb6eed91cde12ab712b3e24337ef0905cbe4bcc539bff9";

    private static T RoundTrip<T>(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json, ContractJson.Options);
        Assert.NotNull(value);
        var back = JsonSerializer.Serialize(value, ContractJson.Options);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(back)), $"round trip changed the JSON: {back}");
        return value;
    }

    [Fact]
    public void Profile_list_marks_damaged_files_without_name_or_game()
    {
        var list = RoundTrip<ProfileList>(
            """{"activeId":"ets2-starter-1a2b","profiles":[{"id":"ets2-starter-1a2b","damaged":false,"name":"ETS2 starter","game":"ets2"},{"id":"broken-0000","damaged":true}]}""");
        Assert.Equal(Game.Ets2, list.Profiles[0].Game);
        Assert.Null(list.Profiles[1].Name);

        // No active profile: the field is absent, never null or empty.
        Assert.Equal("""{"profiles":[]}""", JsonSerializer.Serialize(new ProfileList { Profiles = [] }, ContractJson.Options));
    }

    [Fact]
    public void Create_request_and_problem_list_round_trip()
    {
        Assert.Equal(Game.Ats, RoundTrip<CreateProfileRequest>("""{"name":"Night drive","game":"ats"}""").Game);
        RoundTrip<ProblemList>("""{"problems":["Layout 'iphone-14-pro', widget 'beacon': smaller than 44 x 44 pt."]}""");
        RoundTrip<CatalogList>("""{"catalogs":[{"schemaVersion":1,"game":"ets2","actions":[{"id":"beacon","name":"Beacon","output":{"kind":"scs","input":"beacon"}}]}]}""");
    }

    [Fact]
    public void Asset_info_uses_wire_type_names()
    {
        var info = RoundTrip<AssetInfo>($$"""{"id":"{{Hash}}","type":"webp","size":1024,"bytes":180000,"usedBy":["ets2-starter-1a2b"]}""");
        Assert.Equal(AssetType.Webp, info.Type);
        RoundTrip<AssetList>($$"""{"assets":[{"id":"{{Hash}}","type":"png","size":512,"bytes":1,"usedBy":[]}]}""");
    }

    [Fact]
    public void Telemetry_settings_and_test_press_round_trip()
    {
        RoundTrip<TelemetryView>("""{"available":true,"gameListening":false,"fields":[{"name":"truck.light.beacon","value":1}]}""");
        RoundTrip<TestPressRequest>("""{"actionId":"beacon"}""");
        var settings = RoundTrip<SettingsStatus>("""{"certificate":{"available":true,"fingerprint":"26EC54A3000000000000000000000000000000000000000000000000DC7A9875"},"firewall":"missing",""" + Scs + "}");
        Assert.Equal(FirewallState.Missing, settings.Firewall);
        RoundTrip<SettingsStatus>("""{"certificate":{"available":false},"firewall":"unknown",""" + Scs + "}");
        Assert.Equal("REGENERATE", RoundTrip<RegenerateCaRequest>("""{"confirm":"REGENERATE"}""").Confirm);
    }

    // Phase 5: the SCS plugin panel (decisions §11, §12). installDir is absent when the game is not found.
    private const string Scs =
        """
        "scsPlugin":{"included":true,"games":[{"game":"ets2","state":"upToDate","installDir":"C:\\Games\\Euro Truck Simulator 2","connected":true},{"game":"ats","state":"gameNotFound","connected":false}]}
        """;

    [Fact]
    public void Scs_plugin_status_and_install_request_round_trip()
    {
        var settings = RoundTrip<SettingsStatus>("""{"certificate":{"available":false},"firewall":"present",""" + Scs + "}");
        Assert.Equal(ScsPluginState.UpToDate, settings.ScsPlugin.Games[0].State);
        Assert.Null(settings.ScsPlugin.Games[1].InstallDir);
        Assert.Equal(ScsGame.Ats, RoundTrip<ScsInstallRequest>("""{"game":"ats"}""").Game);
        RoundTrip<ScsGameStatus>("""{"game":"ats","state":"updateAvailable","installDir":"D:\\x","connected":false}""");
        RoundTrip<ScsGameStatus>("""{"game":"ets2","state":"notInstalled","connected":false}""");
    }

    // The editor is trusted more than a phone, but a bug or a hostile page must still not slip extra or numeric values through.
    [Theory]
    [InlineData(typeof(AssetInfo), """{"id":"HASH","type":1,"size":512,"bytes":1,"usedBy":[]}""")]
    [InlineData(typeof(AssetInfo), """{"id":"HASH","type":"svg","size":512,"bytes":1,"usedBy":[]}""")]
    [InlineData(typeof(CreateProfileRequest), """{"name":"x","game":"ets2","id":"chosen"}""")]
    [InlineData(typeof(CreateProfileRequest), """{"name":"x"}""")]
    [InlineData(typeof(TestPressRequest), """{"actionId":"beacon","output":{"kind":"scs","input":"x"}}""")]
    [InlineData(typeof(SettingsStatus), """{"certificate":{"available":true},"firewall":"open"}""")]
    [InlineData(typeof(RegenerateCaRequest), """{}""")]
    [InlineData(typeof(SettingsStatus), """{"certificate":{"available":true},"firewall":"present"}""")]
    [InlineData(typeof(ScsInstallRequest), """{"game":"acc"}""")]
    [InlineData(typeof(ScsInstallRequest), """{"game":"ets2","path":"C:\\x"}""")]
    [InlineData(typeof(ScsInstallRequest), """{}""")]
    [InlineData(typeof(ScsGameStatus), """{"game":"ets2","state":"installed","connected":true}""")]
    [InlineData(typeof(ScsGameStatus), """{"game":"ets2","state":"upToDate"}""")]
    public void Malformed_editor_bodies_are_rejected(Type type, string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json.Replace("HASH", Hash), type, ContractJson.Options));

    // Grid limits (decisions §6) are part of the schema; the generated type keeps plain ints, so the hand-written validator
    // (Security ProfileValidator) enforces them. This only pins that 24 x 16 still parses.
    [Fact]
    public void Largest_grid_parses()
    {
        var layout = JsonSerializer.Deserialize<Layout>("""{"schemaVersion":1,"templateId":"a","grid":{"columns":24,"rows":16},"widgets":[]}""", ContractJson.Options);
        Assert.Equal(24, layout!.Grid.Columns);
    }
}
