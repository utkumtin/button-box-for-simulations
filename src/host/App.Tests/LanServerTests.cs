using System.Drawing;
using System.Net;
using System.Net.WebSockets;
using ButtonBox.App;
using ButtonBox.App.Lan;
using ButtonBox.Security;
using Xunit;

namespace ButtonBox.App.Tests;

public sealed class LanServerTests : IAsyncDisposable
{
    private readonly LanFixture _fx = new();
    private LanRig? _rig;

    private static CancellationToken Ct => LanFixture.Ct;

    public async ValueTask DisposeAsync()
    {
        if (_rig is not null)
        {
            await _rig.DisposeAsync();
        }

        await _fx.DisposeAsync();
    }

    private async Task<LanRig> StartAsync() => _rig = await LanRig.StartAsync(_fx);

    [Fact]
    public async Task Https_works_under_the_slim_builder_and_serves_the_client_with_security_headers()
    {
        // The LAN server is built with CreateSlimBuilder; this proves TLS really comes up with the A1 certificate and that the
        // phone client is served with the strict CSP (a missing header would let an injected script run on the phone).
        var rig = await StartAsync();
        using var http = rig.CreateClient();

        using var response = await http.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CLIENT-MARKER", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(LocalServer.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Https_also_works_over_http2()
    {
        var rig = await StartAsync();
        using var http = rig.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal("CLIENT-MARKER", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_certificate_from_a_different_ca_is_refused()
    {
        // Guards the test kit itself: if the trust callback accepted anything, every HTTPS test here would pass vacuously.
        var rig = await StartAsync();
        using var otherCa = CertificateAuthority.LoadOrCreate(Path.Combine(_fx.Root, "other-ca"), "bb-test-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(LanFixture.Handler(otherCa.CertificateDer), disposeHandler: true)
        {
            BaseAddress = new Uri($"https://{LanFixture.Label}.local:{rig.HttpsPort}/"),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/", Ct));
    }

    [Fact]
    public async Task A_certificate_for_another_name_is_refused()
    {
        // The phone reaches the PC as <label>.local; a certificate that does not name it must not be accepted.
        var rig = await StartAsync();
        using var client = new HttpClient(LanFixture.Handler(_fx.Ca.CertificateDer), disposeHandler: true)
        {
            BaseAddress = new Uri($"https://buttonbox-ffff.local:{rig.HttpsPort}/"),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/", Ct));
    }

    [Theory]
    [InlineData("/nothing")]
    [InlineData("/editor/")]
    [InlineData("/api/status")]
    [InlineData("/api/templates")]
    [InlineData("/api/devices")]
    [InlineData("/editor/api/status")]
    [InlineData("/editor/api/devices")]
    [InlineData("/editor/api/pairing")]
    public async Task Everything_else_is_404_and_the_editor_api_does_not_exist(string path)
    {
        // Decisions §3: "The editor API never exists on a LAN server."
        var rig = await StartAsync();
        using var http = rig.CreateClient();

        using var response = await http.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(LocalServer.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task The_home_screen_icon_is_the_logo_resized_to_180_pixels()
    {
        var rig = await StartAsync();
        using var http = rig.CreateClient();

        using var response = await http.GetAsync("/apple-touch-icon.png", Ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("max-age=86400", response.Headers.CacheControl?.ToString());
        using var image = new Bitmap(new MemoryStream(bytes));
        Assert.Equal(new Size(180, 180), image.Size);
        Assert.Equal(Color.Orange.ToArgb(), image.GetPixel(90, 90).ToArgb());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Origin_missing_or_foreign_is_refused_before_the_websocket_upgrade(bool http2)
    {
        // A browser always sends Origin, so a page on another site (or no Origin at all) is not our phone client.
        var rig = await StartAsync();
        var port = rig.HttpsPort;
        var refused = new string?[]
        {
            null,
            $"http://{LanFixture.Label}.local:{port}",
            $"https://evil.example:{port}",
            $"https://{LanFixture.Label}.local:{port + 1}",
            $"https://{LanFixture.Label}.local",
            "null",
            $"https://192.168.77.11:{port}",
        };

        foreach (var origin in refused)
        {
            await Assert.ThrowsAsync<WebSocketException>(() => rig.ConnectAsync(http2, origin));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Origin_of_the_mdns_name_or_a_lan_address_is_accepted(bool http2)
    {
        var rig = await StartAsync();
        var port = rig.HttpsPort;

        foreach (var origin in new[] { $"https://{LanFixture.Label}.local:{port}", $"https://{LanFixture.Label.ToUpperInvariant()}.LOCAL:{port}", $"https://192.168.77.10:{port}" })
        {
            using var socket = await rig.ConnectAsync(http2, origin);
            Assert.Equal(WebSocketState.Open, socket.State);
        }
    }

    [Theory]
    [InlineData(null, 403)]
    [InlineData("https://evil.example", 403)]
    [InlineData("http://buttonbox-abcd.local", 403)]
    [InlineData("default", 404)] // allowed: passes the origin check and reaches the (closed) pairing session
    public async Task Pair_endpoint_checks_the_origin(string? origin, int expected)
    {
        var rig = await StartAsync();
        using var http = rig.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pair") { Content = LanRig.Json(LanRig.PairJson("123456")) };
        var effective = origin == "default" ? rig.DefaultOrigin : origin;
        if (effective is not null)
        {
            request.Headers.Add("Origin", effective);
        }

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Fact]
    public async Task Pair_endpoint_accepts_the_lan_address_origin_too()
    {
        var rig = await StartAsync();
        using var http = rig.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pair") { Content = LanRig.Json(LanRig.PairJson("123456")) };
        request.Headers.Add("Origin", $"https://192.168.77.10:{rig.HttpsPort}");

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Pair_endpoint_only_answers_post()
    {
        var rig = await StartAsync();
        using var http = rig.CreateClient();

        using var response = await http.GetAsync("/api/pair", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
