using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ButtonBox.App.Lan;
using ButtonBox.App.Profiles;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Simulator;
using Xunit;

namespace ButtonBox.App.Tests;

/// <summary>The loopback server with the Phase 4 parts wired to real stores in a temp folder, optionally with a phone on a real LAN server.</summary>
internal sealed class Api4Rig : IAsyncDisposable
{
    private readonly LanFixture _fx = new();
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
    private readonly List<WebSocket> _sockets = [];

    private Api4Rig()
    {
    }

    public EditorSecret_ Secret { get; } = new();

    public ScsSimulator Sim { get; } = new();

    public ProfileStore Store { get; private set; } = null!;

    public AssetLibrary Assets { get; private set; } = null!;

    public GameSession Session { get; private set; } = null!;

    public LanRig? Lan { get; private set; }

    public LocalServer Server { get; private set; } = null!;

    public LanFixture Fx => _fx;

    public string DataDir => Path.Combine(_fx.Root, "data");

    public string ActiveId { get; private set; } = "";

    public string Origin => $"http://127.0.0.1:{Server.BaseAddress.Port}";

    public Func<string?>? Fingerprint { get; set; } = () => new string('A', 64);

    public Func<Task<CaRegenerationResult>>? Regenerate { get; set; }

    public sealed class EditorSecret_
    {
        private readonly ButtonBox.Security.EditorSecret _inner = ButtonBox.Security.EditorSecret.Create();

        public string Value => _inner.Value;

        public ButtonBox.Security.EditorSecret Inner => _inner;
    }

    public static async Task<Api4Rig> StartAsync(bool phone = false, IOutputBackend? backend = null, IStateProvider? state = null, bool wired = true, IFirewallStatus? firewall = null)
    {
        var rig = new Api4Rig();
        var catalogs = new CatalogStore(Path.Combine(AppContext.BaseDirectory, "catalog"));
        rig.Assets = new AssetLibrary(Path.Combine(rig.DataDir, "assets"));
        var seed = DemoContent.ReadSeed();
        foreach (var image in seed.Images)
        {
            Assert.Null(rig.Assets.Import(image).Problem);
        }

        rig.Session = new GameSession(null, backend ?? rig.Sim, rig.Sim);
        rig.Store = new ProfileStore(rig.DataDir, new ProfileGate(catalogs, rig._fx.Templates, rig.Assets), catalogs, rig.Session);
        var imported = rig.Store.Import(JsonSerializer.SerializeToUtf8Bytes(seed.Profile, ContractJson.Options));
        Assert.True(imported.Ok, string.Join(' ', imported.Problems));
        rig.ActiveId = imported.Value!.Id;
        Assert.True(rig.Store.Activate(rig.ActiveId).Ok);
        if (phone)
        {
            rig.Lan = await LanRig.StartAsync(rig._fx, new DeviceConnectionsOptions { Time = rig._fx.Time, MessagesPerSecond = 100_000 }, session: rig.Session, assets: rig.Assets);
        }

        var editorDir = Path.Combine(rig._fx.Root, "editor");
        Directory.CreateDirectory(editorDir);
        rig.Server = await LocalServer.StartAsync(rig._fx.ClientDir, editorDir, new EditorApiOptions
        {
            Secret = rig.Secret.Inner,
            Registry = rig._fx.Registry,
            Templates = rig._fx.Templates,
            Pairing = () => rig.Lan?.Pairing,
            IsConnected = id => rig.Lan?.Connections.IsConnected(id) ?? false,
            Status = () => new HostStatus { MdnsName = LanFixture.Label, LanServer = LanServerState.Running, Addresses = [] },
            Profiles = wired ? rig.Store : null,
            Catalogs = wired ? catalogs : null,
            Assets = wired ? rig.Assets : null,
            Session = wired ? rig.Session : null,
            Telemetry = wired ? state ?? rig.Sim : null,
            CaFingerprint = wired ? () => rig.Fingerprint?.Invoke() : null,
            Firewall = wired ? firewall ?? new FakeFirewall(FirewallState.Present) : null,
            RegenerateCa = wired ? () => rig.Regenerate is { } r ? r() : Task.FromResult(CaRegenerationResult.Unavailable) : null,
        });
        return rig;
    }

    public HttpRequestMessage Build(string method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), new Uri(Server.BaseAddress, path));
        request.Headers.TryAddWithoutValidation("Cookie", EditorApi.CookieName + "=" + Secret.Value);
        if (method == "POST")
        {
            request.Headers.TryAddWithoutValidation("Origin", Origin);
            request.Content = new StringContent(json ?? "{}", Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        return request;
    }

    public HttpRequestMessage BuildBytes(string path, byte[] bytes, string contentType)
    {
        var request = Build("POST", path);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return request;
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _http.SendAsync(request, LanFixture.Ct);

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(Build("GET", path));

    public Task<HttpResponseMessage> PostAsync(string path, string? json = null) => SendAsync(Build("POST", path, json));

    public async Task<T> GetJsonAsync<T>(string path)
    {
        using var response = await GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(LanFixture.Ct), ContractJson.Options)!;
    }

    public async Task<Profile> SaveRelabelAsync(string id, string widgetId, string label)
    {
        var profile = await GetJsonAsync<Profile>("/editor/api/profiles/" + id);
        profile.Layouts[0].Widgets.Single(w => w.Id == widgetId).Label = label;
        using var response = await PostAsync($"/editor/api/profiles/{id}/save", JsonSerializer.Serialize(profile, ContractJson.Options));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return profile;
    }

    public async Task<PhoneFrames> ConnectPhoneAsync()
    {
        var (_, token) = _fx.Registry.Add("Phone " + _sockets.Count, LanFixture.TemplateId, false);
        var socket = await Lan!.ConnectAsync();
        _sockets.Add(socket);
        await LanRig.SendTextAsync(socket, LanRig.Hello(token));
        return new PhoneFrames(socket);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sockets)
        {
            s.Dispose();
        }

        _http.Dispose();
        await Server.DisposeAsync();
        if (Lan is not null)
        {
            await Lan.DisposeAsync();
        }

        await Session.DisposeAsync();
        await _fx.DisposeAsync();
    }
}

/// <summary>Collects every text frame of a phone socket in the background, so a test can wait for one without cancelling a receive.</summary>
internal sealed class PhoneFrames
{
    private readonly ConcurrentQueue<string> _frames = new();

    public PhoneFrames(WebSocket socket)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    _frames.Enqueue(await LanRig.ReadFrameAsync(socket));
                }
            }
            catch (Exception)
            {
                // Closed or disposed at the end of the test.
            }
        });
    }

    public IReadOnlyList<string> All => [.. _frames];

    public IReadOnlyList<string> Layouts => [.. _frames.Where(f => f.Contains("\"type\":\"layout\"", StringComparison.Ordinal))];

    public Task WaitLayoutsAsync(int count, string? contains = null) =>
        LanRig.WaitForAsync(() => Layouts, l => l.Count >= count && (contains is null || l[^1].Contains(contains, StringComparison.Ordinal)));
}

internal sealed class FakeFirewall(FirewallState state) : IFirewallStatus
{
    public Task<FirewallState> GetAsync() => Task.FromResult(state);
}

internal sealed class FakeState(int fields) : IStateProvider
{
    public TelemetrySnapshot Current { get; } = new(
        true,
        false,
        Enumerable.Range(0, fields).ToDictionary(i => "f" + (fields - i).ToString("D4", System.Globalization.CultureInfo.InvariantCulture), i => i));

    public event EventHandler<TelemetrySnapshot>? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>A backend that never finishes a press, so the output queue fills up.</summary>
internal sealed class StuckBackend : IOutputBackend
{
    public Task PressAsync(GameOutput output, CancellationToken cancellationToken = default) => Task.Delay(Timeout.Infinite, cancellationToken);

    public Task HoldAsync(GameOutput output, CancellationToken cancellationToken = default) => Task.Delay(Timeout.Infinite, cancellationToken);

    public Task ReleaseAsync(GameOutput output, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class EditorApiProfileTests
{
    private const string Api = "/editor/api";

    private static CancellationToken Ct => LanFixture.Ct;

    private static async Task<string> BodyAsync(HttpResponseMessage r) => await r.Content.ReadAsStringAsync(Ct);

    private static byte[] Png(int size, int seed)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(seed % 256, (seed * 7) % 256, (seed * 13) % 256));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static string Pad(string json, int total) => json + new string(' ', total - Encoding.UTF8.GetByteCount(json));

    private static HttpRequestMessage Chunked(Api4Rig rig, string path, int bytes, string contentType = "application/json")
    {
        var request = rig.Build("POST", path);
        request.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(Pad("{}", bytes))));
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Headers.TransferEncodingChunked = true;
        return request;
    }

    // ---- gates that apply to every new route ----

    public static TheoryData<string, string> AllNewRoutes => new()
    {
        { "GET", "/catalogs" },
        { "GET", "/profiles" },
        { "GET", "/profiles/demo" },
        { "GET", "/profiles/demo/export" },
        { "POST", "/profiles/create" },
        { "POST", "/profiles/import" },
        { "POST", "/profiles/demo/save" },
        { "POST", "/profiles/demo/duplicate" },
        { "POST", "/profiles/demo/delete" },
        { "POST", "/profiles/demo/activate" },
        { "GET", "/assets" },
        { "POST", "/assets/upload" },
        { "GET", "/assets/0000000000000000000000000000000000000000000000000000000000000000" },
        { "POST", "/assets/0000000000000000000000000000000000000000000000000000000000000000/delete" },
        { "GET", "/telemetry" },
        { "POST", "/test" },
        { "GET", "/settings" },
        { "POST", "/settings/regenerate-ca" },
    };

    [Theory]
    [MemberData(nameof(AllNewRoutes))]
    public async Task Every_new_route_needs_the_cookie(string method, string path)
    {
        // The user's own browser has no cookie; no route may answer (or even say it exists) without it.
        await using var rig = await Api4Rig.StartAsync();
        var request = rig.Build(method, Api + path);
        request.Headers.Remove("Cookie");

        using var response = await rig.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(string.Empty, await BodyAsync(response));
    }

    [Theory]
    [MemberData(nameof(AllNewRoutes))]
    public async Task Every_new_post_route_needs_a_matching_origin(string method, string path)
    {
        if (method != "POST")
        {
            return;
        }

        await using var rig = await Api4Rig.StartAsync();
        var request = rig.Build(method, Api + path);
        request.Headers.Remove("Origin");

        using var response = await rig.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_cross_site_form_cannot_upload_or_post_json_routes()
    {
        // A form can send text/plain and multipart/form-data without a preflight. Neither may reach any body-reading route,
        // and a JSON body is no image.
        await using var rig = await Api4Rig.StartAsync();
        var png = Png(512, 1);
        foreach (var type in new[] { "text/plain", "multipart/form-data; boundary=x", "application/x-www-form-urlencoded", "application/json", "image/png; charset=utf-8", "image/svg+xml", "image/gif" })
        {
            using var response = await rig.SendAsync(rig.BuildBytes(Api + "/assets/upload", png, type));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }

        foreach (var path in new[] { "/profiles/create", "/profiles/import", "/profiles/demo/save", "/test", "/settings/regenerate-ca", "/profiles/demo/delete" })
        {
            foreach (var type in new[] { "text/plain", "multipart/form-data; boundary=x", "image/png" })
            {
                using var response = await rig.SendAsync(rig.BuildBytes(Api + path, Encoding.UTF8.GetBytes("{}"), type));
                Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            }
        }

        Assert.DoesNotContain(rig.Assets.List(), a => a.Bytes == png.Length);
    }

    [Fact]
    public async Task A_wrong_method_on_a_new_route_is_405()
    {
        await using var rig = await Api4Rig.StartAsync();

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.PostAsync(Api + "/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.GetAsync(Api + "/test")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.GetAsync(Api + "/profiles/demo/save")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.PostAsync(Api + "/profiles/demo")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.GetAsync(Api + "/settings/regenerate-ca")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.GetAsync(Api + "/profiles/demo/nope")).StatusCode);
    }

    [Fact]
    public async Task Routes_of_a_part_that_does_not_exist_answer_503()
    {
        await using var rig = await Api4Rig.StartAsync(wired: false);

        foreach (var (method, path) in new[] { ("GET", "/catalogs"), ("GET", "/profiles"), ("GET", "/assets"), ("GET", "/telemetry"), ("GET", "/settings"), ("POST", "/profiles/create"), ("POST", "/test") })
        {
            var body = path == "/test" ? """{"actionId":"starter"}""" : """{"name":"x","game":"ets2"}""";
            using var response = await rig.SendAsync(rig.Build(method, Api + path, body));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        using var regenerate = await rig.PostAsync(Api + "/settings/regenerate-ca", """{"confirm":"REGENERATE"}""");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, regenerate.StatusCode);
    }

    // ---- body caps ----

    [Theory]
    [InlineData("/profiles/create", 4096)]
    [InlineData("/test", 4096)]
    [InlineData("/profiles/demo/delete", 4096)]
    [InlineData("/profiles/import", 1024 * 1024)]
    [InlineData("/profiles/demo/save", 1024 * 1024)]
    public async Task A_body_over_the_cap_of_its_route_is_413_declared_or_chunked(string path, int cap)
    {
        // Every route reads its body with its own cap; an unbounded read would let any local process hold memory.
        await using var rig = await Api4Rig.StartAsync();

        using var declared = await rig.PostAsync(Api + path, Pad("{}", cap + 1));
        using var chunked = await rig.SendAsync(Chunked(rig, Api + path, cap + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
    }

    [Fact]
    public async Task A_save_of_exactly_1_MiB_is_still_read()
    {
        await using var rig = await Api4Rig.StartAsync();
        var profile = await rig.GetJsonAsync<Profile>($"{Api}/profiles/{rig.ActiveId}");

        using var response = await rig.PostAsync($"{Api}/profiles/{rig.ActiveId}/save", Pad(JsonSerializer.Serialize(profile, ContractJson.Options), EditorApi.MaxProfileBytes));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task An_upload_over_4_MiB_is_413_declared_or_chunked()
    {
        await using var rig = await Api4Rig.StartAsync();
        var big = new byte[EditorApi.MaxImageBytes + 1];

        using var declared = await rig.SendAsync(rig.BuildBytes(Api + "/assets/upload", big, "image/png"));
        var chunkedRequest = rig.BuildBytes(Api + "/assets/upload", big, "image/png");
        chunkedRequest.Content = new StreamContent(new MemoryStream(big));
        chunkedRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        chunkedRequest.Headers.TransferEncodingChunked = true;
        using var chunked = await rig.SendAsync(chunkedRequest);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
    }

    // ---- path ids ----

    [Theory]
    [InlineData("%2E%2E%2Fsecret")]
    [InlineData("..%5Csecret")]
    [InlineData("UPPER")]
    [InlineData("a%2Fb")]
    [InlineData("-lead")]
    public async Task An_id_that_is_not_an_id_is_404_and_never_reaches_the_disk(string id)
    {
        // Ids become file names. A traversal that reached the store could delete or overwrite a file outside profiles\.
        await using var rig = await Api4Rig.StartAsync();
        var sentinel = Path.Combine(rig.DataDir, "secret.json");
        await File.WriteAllTextAsync(sentinel, "{}", Ct);

        foreach (var path in new[] { $"/profiles/{id}/delete", $"/profiles/{id}/activate", $"/profiles/{id}/duplicate", $"/profiles/{id}/save" })
        {
            using var response = await rig.PostAsync(Api + path, "{}");
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, path + " " + response.StatusCode);
        }

        using var get = await rig.GetAsync($"{Api}/profiles/{id}");
        using var export = await rig.GetAsync($"{Api}/profiles/{id}/export");
        using var asset = await rig.GetAsync($"{Api}/assets/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, export.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asset.StatusCode);
        Assert.True(File.Exists(sentinel));
        Assert.True(rig.Store.Get(rig.ActiveId).Ok);
    }

    // ---- profiles ----

    [Fact]
    public async Task Catalogs_and_profiles_are_listed_with_the_active_one()
    {
        await using var rig = await Api4Rig.StartAsync();

        var catalogs = await rig.GetJsonAsync<CatalogList>(Api + "/catalogs");
        var profiles = await rig.GetJsonAsync<ProfileList>(Api + "/profiles");

        Assert.Contains(catalogs.Catalogs, c => c.Game == Game.Ets2);
        Assert.Equal(rig.ActiveId, profiles.ActiveId);
        Assert.Contains(profiles.Profiles, p => p.Id == rig.ActiveId && !p.Damaged);
    }

    [Fact]
    public async Task Get_returns_the_profile_404_for_an_unknown_id_and_409_for_a_damaged_file()
    {
        await using var rig = await Api4Rig.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(rig.DataDir, "profiles", "broken.json"), "not json", Ct);

        var profile = await rig.GetJsonAsync<Profile>($"{Api}/profiles/{rig.ActiveId}");

        Assert.Equal(rig.ActiveId, profile.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.GetAsync(Api + "/profiles/nobody")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await rig.GetAsync(Api + "/profiles/broken")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await rig.PostAsync(Api + "/profiles/broken/duplicate")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await rig.PostAsync(Api + "/profiles/broken/activate")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await rig.PostAsync(Api + "/profiles/broken/save", JsonSerializer.Serialize(profile, ContractJson.Options))).StatusCode);
    }

    [Fact]
    public async Task Export_is_the_profile_json_as_an_attachment_named_after_its_id()
    {
        await using var rig = await Api4Rig.StartAsync();

        using var response = await rig.GetAsync($"{Api}/profiles/{rig.ActiveId}/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"attachment; filename=\"{rig.ActiveId}.json\"", response.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(rig.ActiveId, JsonSerializer.Deserialize<Profile>(await BodyAsync(response), ContractJson.Options)!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.GetAsync(Api + "/profiles/nobody/export")).StatusCode);
    }

    [Fact]
    public async Task Create_makes_an_empty_profile_and_refuses_what_the_gate_refuses_with_problems()
    {
        await using var rig = await Api4Rig.StartAsync();

        using var created = await rig.PostAsync(Api + "/profiles/create", """{"name":"Night run","game":"ets2"}""");
        using var noCatalog = await rig.PostAsync(Api + "/profiles/create", """{"name":"Race","game":"acc"}""");
        using var noName = await rig.PostAsync(Api + "/profiles/create", """{"name":"","game":"ets2"}""");
        using var broken = await rig.PostAsync(Api + "/profiles/create", """{"name":"x"}""");
        using var unknownGame = await rig.PostAsync(Api + "/profiles/create", """{"name":"x","game":"pong"}""");
        using var nullName = await rig.PostAsync(Api + "/profiles/create", """{"name":null,"game":"ets2"}""");

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var profile = JsonSerializer.Deserialize<Profile>(await BodyAsync(created), ContractJson.Options)!;
        Assert.Equal("Night run", profile.Name);
        Assert.True(rig.Store.Get(profile.Id).Ok);
        // The game has no catalog (or the name is empty): 422 with sentences the editor shows.
        foreach (var refused in new[] { noCatalog, noName })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.NotEmpty(JsonSerializer.Deserialize<ProblemList>(await BodyAsync(refused), ContractJson.Options)!.Problems);
        }

        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownGame.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nullName.StatusCode);
    }

    [Fact]
    public async Task Import_adds_a_copy_under_a_new_id_and_a_bad_file_is_422_or_400()
    {
        await using var rig = await Api4Rig.StartAsync();
        var json = JsonSerializer.Serialize(await rig.GetJsonAsync<Profile>($"{Api}/profiles/{rig.ActiveId}"), ContractJson.Options);

        using var imported = await rig.PostAsync(Api + "/profiles/import", json);
        using var garbage = await rig.PostAsync(Api + "/profiles/import", "{\"nope\":1}");
        using var deep = await rig.PostAsync(Api + "/profiles/import", string.Concat(Enumerable.Repeat("[", 5000)));

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var copy = JsonSerializer.Deserialize<Profile>(await BodyAsync(imported), ContractJson.Options)!;
        Assert.NotEqual(rig.ActiveId, copy.Id);
        Assert.Equal(rig.ActiveId, rig.Store.ActiveId); // an import never takes over
        Assert.Equal(HttpStatusCode.UnprocessableEntity, garbage.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, deep.StatusCode);
        Assert.NotEmpty(JsonSerializer.Deserialize<ProblemList>(await BodyAsync(garbage), ContractJson.Options)!.Problems);
    }

    // Gate 4: a malicious layout corpus is rejected at the real entry point. Each body is the seeded profile with one hostile change,
    // so only that change can be the reason; every one must be refused (422 with problems, or 400) and must write no profile file.
    [Theory]
    [InlineData("unknown field", """{"onClick":"alert(1)"}""", "widget")]
    [InlineData("control character in a label", """{"label":"A<NUL>B"}""", "widget")]
    [InlineData("path traversal as an action", """{"action":"../../x"}""", "widget")]
    [InlineData("external image url", """{"look":{"mode":"images","image":"https://evil.example/x.png"}}""", "widget")]
    [InlineData("image that is not in the library", """{"look":{"mode":"images","image":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}""", "widget")]
    [InlineData("overlap", """{"cell":{"col":0,"row":0,"colSpan":2,"rowSpan":2}}""", "widget")]
    [InlineData("grid over the limit", """{"columns":25,"rows":3}""", "grid")]
    [InlineData("numeric enum", """{"game":0}""", "profile")]
    public async Task A_hostile_import_is_refused_and_writes_nothing(string why, string patch, string target)
    {
        _ = why;
        await using var rig = await Api4Rig.StartAsync();
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(await rig.GetJsonAsync<Profile>($"{Api}/profiles/{rig.ActiveId}"), ContractJson.Options))!;
        // <NUL> becomes the JSON escape for U+0000, so the label holds a real U+0000 after parsing.
        var changes = System.Text.Json.Nodes.JsonNode.Parse(patch.Replace("<NUL>", "\\u0000", StringComparison.Ordinal))!.AsObject();
        var layout = node["layouts"]![0]!;
        var obj = target switch
        {
            "profile" => node.AsObject(),
            "grid" => layout["grid"]!.AsObject(),
            // The beacon toggle: a toggle has action, label and look, and the overlap cell lands on the engine button.
            _ => layout["widgets"]!.AsArray().Single(w => (string?)w!["id"] == "beacon")!.AsObject(),
        };
        foreach (var (key, value) in changes.ToList())
        {
            obj[key] = value?.DeepClone();
        }

        var before = rig.Store.List().Profiles.Count;
        using var response = await rig.PostAsync(Api + "/profiles/import", node.ToJsonString());

        Assert.True(response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest, $"{why}: {response.StatusCode}");
        Assert.Equal(before, rig.Store.List().Profiles.Count);
    }

    [Fact]
    public async Task Save_stores_the_profile_and_refuses_a_foreign_id_a_changed_game_and_an_invalid_profile()
    {
        await using var rig = await Api4Rig.StartAsync();
        var id = rig.ActiveId;
        var profile = await rig.SaveRelabelAsync(id, "lights", "Lamps");
        Assert.Equal("Lamps", rig.Store.Get(id).Value!.Layouts[0].Widgets.Single(w => w.Id == "lights").Label);

        var other = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile, ContractJson.Options), ContractJson.Options)!;
        other.Id = "someone-else";
        using var idMismatch = await rig.PostAsync($"{Api}/profiles/{id}/save", JsonSerializer.Serialize(other, ContractJson.Options));
        var otherGame = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile, ContractJson.Options), ContractJson.Options)!;
        otherGame.Game = Game.Acc;
        using var gameChanged = await rig.PostAsync($"{Api}/profiles/{id}/save", JsonSerializer.Serialize(otherGame, ContractJson.Options));
        var invalid = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile, ContractJson.Options), ContractJson.Options)!;
        invalid.Layouts[0].Widgets.Single(w => w.Id == "lights").Label = "";
        using var invalidProfile = await rig.PostAsync($"{Api}/profiles/{id}/save", JsonSerializer.Serialize(invalid, ContractJson.Options));
        using var missing = await rig.PostAsync(Api + "/profiles/nobody/save", JsonSerializer.Serialize(new Profile { SchemaVersion = 1, Id = "nobody", Name = "x", Game = Game.Ets2, Layouts = [] }, ContractJson.Options));
        using var malformed = await rig.PostAsync($"{Api}/profiles/{id}/save", "{\"id\":");

        Assert.Equal(HttpStatusCode.Conflict, idMismatch.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, gameChanged.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidProfile.StatusCode);
        Assert.NotEmpty(JsonSerializer.Deserialize<ProblemList>(await BodyAsync(invalidProfile), ContractJson.Options)!.Problems);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("Lamps", rig.Store.Get(id).Value!.Layouts[0].Widgets.Single(w => w.Id == "lights").Label); // none of the refusals changed it
    }

    [Fact]
    public async Task Duplicate_delete_and_activate_follow_the_store_rules()
    {
        await using var rig = await Api4Rig.StartAsync();

        using var duplicated = await rig.PostAsync($"{Api}/profiles/{rig.ActiveId}/duplicate");
        var copy = JsonSerializer.Deserialize<Profile>(await BodyAsync(duplicated), ContractJson.Options)!;
        using var deleteActive = await rig.PostAsync($"{Api}/profiles/{rig.ActiveId}/delete");
        using var activate = await rig.PostAsync($"{Api}/profiles/{copy.Id}/activate");
        using var deleteOld = await rig.PostAsync($"{Api}/profiles/{rig.ActiveId}/delete");
        using var deleteAgain = await rig.PostAsync($"{Api}/profiles/{rig.ActiveId}/delete");
        using var activateUnknown = await rig.PostAsync(Api + "/profiles/nobody/activate");
        using var duplicateUnknown = await rig.PostAsync(Api + "/profiles/nobody/duplicate");

        Assert.Equal(HttpStatusCode.OK, duplicated.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteActive.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);
        Assert.Equal(copy.Id, rig.Store.ActiveId);
        Assert.Equal(HttpStatusCode.NoContent, deleteOld.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, activateUnknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, duplicateUnknown.StatusCode);
    }

    // ---- what a phone sees ----

    [Fact]
    public async Task Saving_the_active_profile_reaches_a_connected_phone_as_a_new_layout_and_saving_another_does_not()
    {
        await using var rig = await Api4Rig.StartAsync(phone: true);
        var phone = await rig.ConnectPhoneAsync();
        await phone.WaitLayoutsAsync(1);
        using var copy = await rig.PostAsync($"{Api}/profiles/{rig.ActiveId}/duplicate");
        var other = JsonSerializer.Deserialize<Profile>(await BodyAsync(copy), ContractJson.Options)!;
        var engineBefore = rig.Session.Engine;

        await rig.SaveRelabelAsync(other.Id, "lights", "Not for the phone");

        Assert.Same(engineBefore, rig.Session.Engine); // a profile that is not active is never published
        await rig.SaveRelabelAsync(rig.ActiveId, "lights", "Lamps");
        await phone.WaitLayoutsAsync(2, "Lamps");
        Assert.DoesNotContain(phone.Layouts, f => f.Contains("Not for the phone", StringComparison.Ordinal));
        Assert.NotSame(engineBefore, rig.Session.Engine);
    }

    [Fact]
    public async Task Activating_a_profile_switches_the_layout_of_a_connected_phone()
    {
        await using var rig = await Api4Rig.StartAsync(phone: true);
        var phone = await rig.ConnectPhoneAsync();
        await phone.WaitLayoutsAsync(1);
        using var copy = await rig.PostAsync($"{Api}/profiles/{rig.ActiveId}/duplicate");
        var other = JsonSerializer.Deserialize<Profile>(await BodyAsync(copy), ContractJson.Options)!;
        await rig.SaveRelabelAsync(other.Id, "lights", "Second profile");

        using var activate = await rig.PostAsync($"{Api}/profiles/{other.Id}/activate");

        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);
        await phone.WaitLayoutsAsync(2, "Second profile");
    }

    // ---- images ----

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"http://evil.example/x.png\"/></svg>")]
    [InlineData("<!DOCTYPE html><html><body><script>alert(1)</script></body></html>")]
    public async Task A_malicious_svg_sent_as_a_png_is_422_and_nothing_is_stored(string svg)
    {
        // The header type is only a claim. The bytes decide (decisions §7), so a script in an "image" never reaches the library.
        await using var rig = await Api4Rig.StartAsync();
        var before = rig.Assets.List().Count;

        using var response = await rig.SendAsync(rig.BuildBytes(Api + "/assets/upload", Encoding.UTF8.GetBytes(svg), "image/png"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.NotEmpty(JsonSerializer.Deserialize<ProblemList>(await BodyAsync(response), ContractJson.Options)!.Problems);
        Assert.Equal(before, rig.Assets.List().Count);
    }

    [Fact]
    public async Task A_valid_png_is_stored_listed_served_with_nosniff_and_deleted_when_unused()
    {
        await using var rig = await Api4Rig.StartAsync();
        var png = Png(512, 5);

        using var upload = await rig.SendAsync(rig.BuildBytes(Api + "/assets/upload", png, "image/png"));

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var info = JsonSerializer.Deserialize<AssetInfo>(await BodyAsync(upload), ContractJson.Options)!;
        Assert.Equal(AssetType.Png, info.Type);
        Assert.Equal(512, info.Size);
        Assert.Equal(png.Length, info.Bytes);
        Assert.Empty(info.UsedBy);

        var list = await rig.GetJsonAsync<AssetList>(Api + "/assets");
        Assert.Contains(list.Assets, a => a.Id == info.Id);
        Assert.Contains(list.Assets, a => a.UsedBy.Contains(rig.ActiveId)); // the demo images are used by the demo profile

        using var served = await rig.GetAsync($"{Api}/assets/{info.Id}");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(png, await served.Content.ReadAsByteArrayAsync(Ct));

        using var deleted = await rig.PostAsync($"{Api}/assets/{info.Id}/delete");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.GetAsync($"{Api}/assets/{info.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.PostAsync($"{Api}/assets/{info.Id}/delete")).StatusCode);
    }

    [Fact]
    public async Task An_image_that_a_profile_uses_cannot_be_deleted()
    {
        await using var rig = await Api4Rig.StartAsync();
        var used = (await rig.GetJsonAsync<AssetList>(Api + "/assets")).Assets.First(a => a.UsedBy.Count > 0);

        using var response = await rig.PostAsync($"{Api}/assets/{used.Id}/delete");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(rig.Assets.Contains(used.Id));
    }

    [Fact]
    public async Task An_asset_id_that_is_not_a_sha256_is_404()
    {
        await using var rig = await Api4Rig.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await rig.GetAsync(Api + "/assets/abc")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.PostAsync(Api + "/assets/abc/delete")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.GetAsync(Api + "/assets/" + new string('A', 64))).StatusCode);
    }

    // ---- telemetry and test mode ----

    [Fact]
    public async Task Telemetry_lists_the_fields_sorted_by_name_and_at_most_512()
    {
        await using var rig = await Api4Rig.StartAsync(state: new FakeState(600));

        var view = await rig.GetJsonAsync<TelemetryView>(Api + "/telemetry");

        Assert.True(view.Available);
        Assert.False(view.GameListening);
        Assert.Equal(512, view.Fields.Count);
        Assert.Equal(view.Fields.Select(f => f.Name).Order(StringComparer.Ordinal), view.Fields.Select(f => f.Name));
        Assert.Equal("f0001", view.Fields[0].Name);
    }

    [Fact]
    public async Task A_test_press_reaches_the_simulator_even_while_the_game_is_not_listening()
    {
        // Decisions §11: test mode skips only the "game not listening" check, so a test works while the game is paused.
        await using var rig = await Api4Rig.StartAsync();
        rig.Sim.GameListening = false;

        using var response = await rig.PostAsync(Api + "/test", """{"actionId":"light-on"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await LanRig.WaitForAsync(() => rig.Sim.Received.Count, n => n >= 1);
        Assert.Contains(rig.Sim.Received, r => r.Output is ScsInput { Name: "lighton" });
    }

    [Fact]
    public async Task A_test_press_without_an_active_profile_is_409_an_unknown_action_404_and_a_bad_body_400()
    {
        await using var rig = await Api4Rig.StartAsync();

        using var unknown = await rig.PostAsync(Api + "/test", """{"actionId":"no-such-action"}""");
        using var bad = await rig.PostAsync(Api + "/test", """{"actionId":"../x"}""");
        using var missing = await rig.PostAsync(Api + "/test", "{}");
        var other = JsonSerializer.Deserialize<Profile>(await BodyAsync(await rig.PostAsync($"{Api}/profiles/{rig.ActiveId}/duplicate")), ContractJson.Options)!;
        // Deleting the active profile is refused, so an empty session is made by activating nothing: a fresh session has no engine.
        await using var empty = await Api4Rig.StartAsync();
        empty.Session.Publish(null, keepSwitchPositions: false);
        using var none = await empty.PostAsync(Api + "/test", """{"actionId":"light-on"}""");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        Assert.NotEqual(rig.ActiveId, other.Id);
    }

    [Fact]
    public async Task A_test_press_is_503_when_the_output_queue_is_full()
    {
        await using var rig = await Api4Rig.StartAsync(backend: new StuckBackend());
        var seen = new HashSet<HttpStatusCode>();

        for (var i = 0; i < GameSession.OutputQueueCapacity + 10; i++)
        {
            using var response = await rig.PostAsync(Api + "/test", """{"actionId":"light-on"}""");
            seen.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.NoContent, seen);
        Assert.Contains(HttpStatusCode.ServiceUnavailable, seen);
    }

    // ---- settings ----

    [Theory]
    [InlineData(FirewallState.Present)]
    [InlineData(FirewallState.Missing)]
    [InlineData(FirewallState.Unknown)]
    public async Task Settings_show_the_ca_fingerprint_and_the_firewall_state(FirewallState state)
    {
        await using var rig = await Api4Rig.StartAsync(firewall: new FakeFirewall(state));

        var settings = await rig.GetJsonAsync<SettingsStatus>(Api + "/settings");

        Assert.True(settings.Certificate.Available);
        Assert.Equal(new string('A', 64), settings.Certificate.Fingerprint);
        Assert.Equal(state, settings.Firewall);
    }

    [Fact]
    public async Task Settings_without_a_ca_say_so_without_a_fingerprint()
    {
        await using var rig = await Api4Rig.StartAsync();
        rig.Fingerprint = () => null;

        var settings = await rig.GetJsonAsync<SettingsStatus>(Api + "/settings");

        Assert.False(settings.Certificate.Available);
        Assert.Null(settings.Certificate.Fingerprint);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"confirm":""}""")]
    [InlineData("""{"confirm":"regenerate"}""")]
    [InlineData("""{"confirm":"REGENERATE "}""")]
    [InlineData("""{"confirm":null}""")]
    [InlineData("not json")]
    public async Task A_wrong_confirmation_is_400_and_nothing_is_regenerated(string body)
    {
        await using var rig = await Api4Rig.StartAsync();
        var calls = 0;
        rig.Regenerate = () =>
        {
            calls++;
            return Task.FromResult(CaRegenerationResult.Done);
        };

        using var response = await rig.PostAsync(Api + "/settings/regenerate-ca", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(CaRegenerationResult.Done, HttpStatusCode.NoContent)]
    [InlineData(CaRegenerationResult.Unavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(CaRegenerationResult.Failed, HttpStatusCode.InternalServerError)]
    public async Task The_confirmed_regeneration_answers_with_its_result(CaRegenerationResult result, HttpStatusCode expected)
    {
        await using var rig = await Api4Rig.StartAsync();
        var calls = 0;
        rig.Regenerate = () =>
        {
            calls++;
            return Task.FromResult(result);
        };

        using var response = await rig.PostAsync(Api + "/settings/regenerate-ca", """{"confirm":"REGENERATE"}""");

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(1, calls);
        Assert.Equal(string.Empty, await BodyAsync(response));
    }
}
