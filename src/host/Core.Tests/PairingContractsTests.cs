using Xunit;
using System.Text.Json;
using System.Text.Json.Nodes;
using ButtonBox.Core.Contracts;

namespace ButtonBox.Core.Tests;

// The Phase 2 contracts (pairing, device templates, editor API) are generated like the protocol. These tests pin
// their wire format: the phone, the editor and the host are written against it by different people in parallel.
public class PairingContractsTests
{
    private const string Screen = """{"width":852,"height":393,"safeArea":{"top":0,"right":59,"bottom":20,"left":59},"pixelRatio":3}""";

    private static T RoundTrip<T>(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json, ContractJson.Options);
        Assert.NotNull(value);
        var back = JsonSerializer.Serialize(value, ContractJson.Options);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(back)), $"round trip changed the JSON: {back}");
        return value;
    }

    [Fact]
    public void Pair_request_and_response_round_trip()
    {
        var request = RoundTrip<PairRequest>($$"""{"code":"012345","screen":{{Screen}}}""");
        // A code with a leading zero must stay a string: as a number it would lose the zero.
        Assert.Equal("012345", request.Code);
        Assert.Equal(59, request.Screen.SafeArea.Left);
        RoundTrip<PairResponse>($$"""{"deviceId":"d-1","token":"{{new string('A', 43)}}"}""");
    }

    // The phone is untrusted: a numeric code, a fractional width or an extra field must fail before any check runs.
    [Theory]
    [InlineData("""{"code":12345,"screen":SCREEN}""")]
    [InlineData("""{"code":"012345","screen":SCREEN,"admin":true}""")]
    [InlineData("""{"code":"012345"}""")]
    [InlineData("""{"code":"012345","screen":{"width":852.5,"height":393,"safeArea":{"top":0,"right":0,"bottom":0,"left":0},"pixelRatio":3}}""")]
    public void Malformed_pair_request_is_rejected(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PairRequest>(json.Replace("SCREEN", Screen), ContractJson.Options));

    [Fact]
    public void Device_template_round_trips_with_wire_enum()
    {
        var template = RoundTrip<DeviceTemplate>(
            $$"""{"schemaVersion":1,"id":"iphone-14-pro","name":"iPhone 14 Pro","screen":{{Screen}},"cutout":"dynamicIsland","generic":false}""");
        Assert.Equal(Cutout.DynamicIsland, template.Cutout);
    }

    [Fact]
    public void Pairing_status_uses_wire_enums_and_omits_absent_fields()
    {
        var closed = JsonSerializer.Serialize(new PairingStatus { State = PairingState.Closed }, ContractJson.Options);
        Assert.Equal("""{"state":"closed"}""", closed);

        var status = RoundTrip<PairingStatus>(
            """{"state":"codeShown","address":"http://buttonbox-7f3a.local","caFingerprint":"26EC54A3000000000000000000000000000000000000000000000000DC7A9875","code":"123456","codeExpiresAt":"2026-11-18T10:02:00+00:00","attemptsLeft":5}""");
        Assert.Equal(PairingState.CodeShown, status.State);
        Assert.Contains("\"failure\":\"codeBurned\"",
            JsonSerializer.Serialize(new PairingStatus { State = PairingState.Failed, Failure = PairingFailure.CodeBurned }, ContractJson.Options));
    }

    // The editor reads codeExpiresAt with JavaScript's Date: both the host's own format and the "Z" form a browser
    // writes must parse to the same instant.
    [Fact]
    public void Code_expiry_is_an_iso_instant()
    {
        var expires = new DateTimeOffset(2026, 11, 18, 10, 2, 0, TimeSpan.Zero);
        var json = JsonSerializer.Serialize(new PairingStatus { State = PairingState.CodeShown, CodeExpiresAt = expires }, ContractJson.Options);
        Assert.Contains("\"codeExpiresAt\":\"2026-11-18T10:02:00+00:00\"", json);
        var fromBrowser = JsonSerializer.Deserialize<PairingStatus>("""{"state":"codeShown","codeExpiresAt":"2026-11-18T10:02:00.000Z"}""", ContractJson.Options);
        Assert.Equal(expires, fromBrowser?.CodeExpiresAt);
    }

    [Fact]
    public void Editor_api_bodies_round_trip()
    {
        RoundTrip<DeviceList>("""{"devices":[{"id":"d-1","name":"Utku's iPhone","templateId":"iphone-14-pro","connected":true,"screenMismatch":false}]}""");
        RoundTrip<RenameDeviceRequest>("""{"name":"Stand"}""");
        Assert.Null(RoundTrip<StartPairingRequest>("{}").TemplateId);
        RoundTrip<StartPairingRequest>("""{"templateId":"iphone-14-pro"}""");
        RoundTrip<TemplateList>($$"""{"templates":[{"schemaVersion":1,"id":"generic-852x393","name":"Generic","screen":{{Screen}},"cutout":"unknown","generic":true}]}""");
        var host = RoundTrip<HostStatus>("""{"mdnsName":"buttonbox-7f3a","lanServer":"portInUse","portOwner":"httpd.exe","addresses":[]}""");
        Assert.Equal(LanServerState.PortInUse, host.LanServer);
    }
}
