using System.Net;
using System.Security.Cryptography;
using ButtonBox.App.Lan;
using ButtonBox.Core.Contracts;
using Xunit;

namespace ButtonBox.App.Tests;

public sealed class AssetRouteTests : IAsyncDisposable
{
    private readonly LanFixture _fx = new();
    private readonly DemoContent _demo = DemoContent.Load();
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

    private async Task<(LanRig Rig, HttpClient Client)> StartAsync()
    {
        _rig = await LanRig.StartAsync(_fx, assets: _demo.Assets);
        return (_rig, _rig.CreateClient());
    }

    // HttpClient and Uri would otherwise decode %2e and drop dot segments before the request leaves, and the test would prove nothing.
    private static Uri Raw(LanRig rig, string pathAndQuery)
    {
        var text = $"https://{LanFixture.Label}.local:{rig.HttpsPort}{pathAndQuery}";
        Assert.True(Uri.TryCreate(text, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }, out var uri));
        return uri;
    }

    [Fact]
    public async Task Every_embedded_image_is_served_under_its_own_hash_with_immutable_caching()
    {
        // The id is the hash of the bytes, so a phone can cache the response forever; it must also be exactly those bytes.
        var (rig, client) = await StartAsync();
        using var _ = client;
        Assert.Equal(3, _demo.Assets.Ids.Count);

        foreach (var id in _demo.Assets.Ids)
        {
            using var response = await client.GetAsync(Raw(rig, "/assets/" + id), Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("public, max-age=31536000, immutable", string.Join(", ", response.Headers.GetValues("Cache-Control")));
            var body = await response.Content.ReadAsByteArrayAsync(Ct);
            Assert.Equal(body.Length, response.Content.Headers.ContentLength);
            Assert.Equal(id, Convert.ToHexStringLower(SHA256.HashData(body)));
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.True(response.Headers.Contains("Content-Security-Policy"));
        }
    }

    [Fact]
    public async Task Head_returns_the_same_headers_and_no_body()
    {
        var (rig, client) = await StartAsync();
        using var _ = client;
        var id = _demo.Assets.Ids.First();
        var expectedLength = _demo.Assets.TryGet(id, out var bytes) ? bytes.Length : -1;

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Raw(rig, "/assets/" + id)), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedLength, response.Content.Headers.ContentLength);
        Assert.Equal("public, max-age=31536000, immutable", string.Join(", ", response.Headers.GetValues("Cache-Control")));
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("suffix")]
    [InlineData("encoded-dots")]
    [InlineData("encoded-id")]
    [InlineData("extra-segment")]
    [InlineData("trailing-slash")]
    [InlineData("query")]
    [InlineData("empty")]
    [InlineData("bare")]
    [InlineData("unknown")]
    [InlineData("dots")]
    [InlineData("path-case")]
    public async Task Anything_but_an_exact_lowercase_hash_is_a_404_and_never_falls_through_to_the_client_files(string kind)
    {
        // Under /assets/ only the exact route may answer: a traversal or a decoded variant must not reach the static files
        // (which would answer 200 with the client's index.html) or anything else on disk.
        var (rig, client) = await StartAsync();
        using var _ = client;
        var id = _demo.Assets.Ids.First();
        var path = kind switch
        {
            "UPPER" => "/assets/" + id.ToUpperInvariant(),
            "short" => "/assets/" + id[..63],
            "long" => "/assets/" + id + "0",
            "suffix" => "/assets/" + id + ".png",
            "encoded-dots" => "/assets/%2e%2e",
            "encoded-id" => "/assets/%" + "30" + id[1..], // the first hex digit percent-encoded
            "extra-segment" => "/assets/" + id + "/x",
            "trailing-slash" => "/assets/" + id + "/",
            "query" => "/assets/" + id + "%3fx=1",
            "empty" => "/assets/",
            "bare" => "/assets",
            "unknown" => "/assets/" + new string('0', 64),
            "dots" => "/assets/..",
            "path-case" => "/ASSETS/" + id,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        using var response = await client.GetAsync(Raw(rig, path), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Other_methods_on_an_asset_are_a_404()
    {
        var (rig, client) = await StartAsync();
        using var _ = client;
        var id = _demo.Assets.Ids.First();

        using var post = await client.PostAsync(Raw(rig, "/assets/" + id), LanRig.Json("{}"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact]
    public async Task The_client_files_are_still_served_next_to_the_asset_route()
    {
        var (_, client) = await StartAsync();
        using var _ = client;

        Assert.Equal("CLIENT-MARKER", await client.GetStringAsync("/", Ct));
    }

    [Fact]
    public async Task Without_an_asset_store_every_asset_request_is_a_404()
    {
        _rig = await LanRig.StartAsync(_fx);
        using var client = _rig.CreateClient();

        using var response = await client.GetAsync(Raw(_rig, "/assets/" + _demo.Assets.Ids.First()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void The_asset_id_pattern_is_the_strict_lowercase_hex_the_decisions_name()
    {
        Assert.True(AssetStore.IsValidId(new string('a', 64)));
        Assert.False(AssetStore.IsValidId(new string('A', 64)));
        Assert.False(AssetStore.IsValidId(new string('a', 64) + "\n")); // "$" would accept a trailing newline, \z does not
        Assert.False(AssetStore.IsValidId(null));
    }
}
