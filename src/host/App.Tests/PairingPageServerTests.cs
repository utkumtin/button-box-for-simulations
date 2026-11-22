using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ButtonBox.App.Lan;
using ButtonBox.App.Pairing;
using Xunit;

namespace ButtonBox.App.Tests;

public sealed class PairingPageServerTests : IAsyncDisposable
{
    private readonly byte[] _der = [1, 2, 3, 4, 5, 250, 251, 252];
    private readonly HttpClient _http = new();
    private PairingPageServer? _server;

    private static CancellationToken Ct => LanFixture.Ct;

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    private async Task<Uri> StartAsync()
    {
        _server = await PairingPageServer.StartAsync([IPAddress.Loopback], 0, _der, "https://buttonbox-abcd.local/");
        return new Uri($"http://127.0.0.1:{_server.Port}/");
    }

    [Fact]
    public async Task The_page_has_no_script_no_inline_style_and_stops_iOS_from_linking_numbers()
    {
        var address = await StartAsync();

        using var response = await _http.GetAsync(address, Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(LocalServer.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("<meta name=\"format-detection\" content=\"telephone=no\">", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"viewport\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"/ca.cer\"", html, StringComparison.Ordinal);
        Assert.Contains("Download Certificate", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://buttonbox-abcd.local/\"", html, StringComparison.Ordinal);
        Assert.Contains("More Details", html, StringComparison.Ordinal);
        Assert.Contains("Certificate Trust Settings", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_shows_no_fingerprint()
    {
        // The comparison is the iOS screen against the PC screen. A fingerprint served over the same HTTP channel as the
        // certificate would prove nothing and invite a wrong sense of security.
        var address = await StartAsync();

        var html = await _http.GetStringAsync(address, Ct);

        Assert.DoesNotMatch(new Regex("[0-9A-Fa-f]{32,}"), html);
    }

    [Fact]
    public async Task The_certificate_is_served_byte_for_byte_with_the_headers_iOS_accepted_in_S1()
    {
        var address = await StartAsync();

        using var response = await _http.GetAsync(new Uri(address, "/ca.cer"), Ct);

        Assert.Equal(_der, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("application/x-x509-ca-cert", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Theory]
    [InlineData("GET", "/ca.crt")]
    [InlineData("GET", "/index.html")]
    [InlineData("GET", "/api/pair")]
    [InlineData("GET", "/ws")]
    [InlineData("GET", "/ca.cer/")]
    [InlineData("POST", "/")]
    [InlineData("POST", "/api/pair")]
    [InlineData("DELETE", "/ca.cer")]
    public async Task Nothing_else_is_served_and_pairing_is_not_possible_here(string method, string path)
    {
        var address = await StartAsync();

        using var response = await _http.SendAsync(new HttpRequestMessage(new HttpMethod(method), new Uri(address, path)), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Disposing_closes_the_port()
    {
        var address = await StartAsync();
        await _server!.DisposeAsync();

        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync(address, Ct));
    }

    [Fact]
    public async Task A_taken_port_is_reported_as_in_use()
    {
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        var port = ((IPEndPoint)occupier.LocalEndpoint).Port;

        var failure = await Record.ExceptionAsync(() => PairingPageServer.StartAsync([IPAddress.Loopback], port, _der, "https://x.local/"));

        Assert.NotNull(failure);
        Assert.True(PortConflict.IsAddressInUse(failure));
    }
}
