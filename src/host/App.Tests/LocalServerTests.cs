using System.Net;
using ButtonBox.App;
using Xunit;

namespace ButtonBox.App.Tests;

public sealed class LocalServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-tests-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LocalServerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "client"));
        Directory.CreateDirectory(Path.Combine(_root, "editor"));
        File.WriteAllText(Path.Combine(_root, "client", "index.html"), "CLIENT-MARKER");
        File.WriteAllText(Path.Combine(_root, "editor", "index.html"), "EDITOR-MARKER");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "TOP-SECRET");
    }

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_root, true);
    }

    private Task<LocalServer> StartAsync() =>
        LocalServer.StartAsync(Path.Combine(_root, "client"), Path.Combine(_root, "editor"));

    [Fact]
    public async Task Binds_to_loopback_only()
    {
        // A LAN listener before TLS exists (Phase 2) would expose an unauthenticated server.
        await using var server = await StartAsync();
        Assert.Equal("127.0.0.1", server.BaseAddress.Host);
        Assert.NotEqual(0, server.BaseAddress.Port);
    }

    [Fact]
    public async Task Serves_client_at_root_and_editor_under_editor()
    {
        await using var server = await StartAsync();
        Assert.Equal("CLIENT-MARKER", await _http.GetStringAsync(new Uri(server.BaseAddress, "/"), Ct));
        Assert.Equal("EDITOR-MARKER", await _http.GetStringAsync(new Uri(server.BaseAddress, "/editor/"), Ct));
    }

    [Fact]
    public async Task Editor_without_trailing_slash_redirects()
    {
        await using var server = await StartAsync();
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var response = await noRedirect.GetAsync(new Uri(server.BaseAddress, "/editor"), Ct);
        Assert.Equal("/editor/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Every_response_has_strict_csp_and_nosniff_including_404()
    {
        // Strict CSP without inline scripts is a security rule (decisions section 9).
        await using var server = await StartAsync();
        foreach (var path in new[] { "/", "/editor/", "/missing.js" })
        {
            var response = await _http.GetAsync(new Uri(server.BaseAddress, path), Ct);
            Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var csp), path);
            Assert.Contains("script-src 'self'", csp!.Single());
            Assert.DoesNotContain("unsafe-inline", csp!.Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.False(response.Headers.Contains("Server"), path);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(new Uri(server.BaseAddress, "/missing.js"), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("/editor/..%2f..%2fsecret.txt")]
    [InlineData("/editor/..%2fsecret.txt")]
    [InlineData("/editor/../secret.txt")]
    [InlineData("/..%2fsecret.txt")]
    [InlineData("/%2e%2e/secret.txt")]
    [InlineData("/editor/%2e%2e%2f..%2fsecret.txt")]
    public async Task Path_traversal_does_not_return_files_outside_content_roots(string path)
    {
        await using var server = await StartAsync();
        var response = await _http.GetAsync(new Uri(server.BaseAddress.GetLeftPart(UriPartial.Authority) + path), Ct);
        Assert.DoesNotContain("TOP-SECRET", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Missing_content_directory_still_starts_and_returns_404()
    {
        // A fresh clone may not have built the web yet.
        await using var server = await LocalServer.StartAsync(
            Path.Combine(_root, "nope-client"), Path.Combine(_root, "nope-editor"));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(new Uri(server.BaseAddress, "/"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(new Uri(server.BaseAddress, "/editor/"), Ct)).StatusCode);
    }
}
