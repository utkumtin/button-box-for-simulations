using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ButtonBox.App.Lan;
using ButtonBox.App.Pairing;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;
using Xunit;

namespace ButtonBox.App.Tests;

/// <summary>A loopback server with the editor API, wired to B1's rig (or to nothing, for the unavailable cases).</summary>
internal sealed class ApiRig : IAsyncDisposable
{
    private readonly LanFixture _fx = new();
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
    private LanRig? _lan;
    private PairingSession? _orphanPairing;

    private ApiRig()
    {
        Secret = EditorSecret.Create();
    }

    public EditorSecret Secret { get; }

    public LanFixture Fx => _fx;

    public LanRig Lan => _lan!;

    public LocalServer Server { get; private set; } = null!;

    public int Port => Server.BaseAddress.Port;

    public string Origin => $"http://127.0.0.1:{Port}";

    public HostStatus Status { get; set; } = new() { MdnsName = LanFixture.Label, LanServer = LanServerState.Running, Addresses = ["192.168.77.10"] };

    /// <summary>mode: "lan" = pairing and connections of a running LanRig; "none" = no LAN side; "noaddress" = a pairing session without a LAN address.</summary>
    public static async Task<ApiRig> StartAsync(string mode = "lan", bool registryLoaded = true)
    {
        var rig = new ApiRig();
        if (mode == "lan")
        {
            rig._lan = await LanRig.StartAsync(rig._fx);
        }
        else if (mode == "noaddress")
        {
            rig._orphanPairing = new PairingSession(new PairingSessionOptions
            {
                Authority = rig._fx.Ca,
                Registry = rig._fx.Registry,
                Templates = rig._fx.Templates,
                MdnsLabel = LanFixture.Label,
                BindAddresses = () => [],
                Time = rig._fx.Time,
            });
        }

        var editorDir = Path.Combine(rig._fx.Root, "editor");
        Directory.CreateDirectory(editorDir);
        File.WriteAllText(Path.Combine(editorDir, "index.html"), "EDITOR-MARKER");
        rig.Server = await LocalServer.StartAsync(rig._fx.ClientDir, editorDir, new EditorApiOptions
        {
            Secret = rig.Secret,
            Registry = registryLoaded ? rig._fx.Registry : null,
            Templates = rig._fx.Templates,
            Pairing = () => rig._lan?.Pairing ?? rig._orphanPairing,
            IsConnected = id => rig._lan?.Connections.IsConnected(id) ?? false,
            Status = () => rig.Status,
        });
        return rig;
    }

    /// <summary>A fully valid request: right Host (HttpClient's default), cookie, and for POST the right Origin and JSON.</summary>
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

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) =>
        await _http.SendAsync(request, LanFixture.Ct);

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(Build("GET", path));

    public Task<HttpResponseMessage> PostAsync(string path, string? json = null) => SendAsync(Build("POST", path, json));

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(LanFixture.Ct));
        return doc.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await Server.DisposeAsync();
        if (_orphanPairing is not null)
        {
            await _orphanPairing.DisposeAsync();
        }

        if (_lan is not null)
        {
            await _lan.DisposeAsync();
        }

        await _fx.DisposeAsync();
    }
}

public sealed class EditorApiTests
{
    private static CancellationToken Ct => LanFixture.Ct;

    private static string Everything(HttpResponseMessage response, string body) =>
        response.Headers.ToString() + response.Content.Headers.ToString() + body;

    // ---- Host header (DNS rebinding) ----

    [Theory]
    [InlineData("/editor/api/status", "evil.example")]
    [InlineData("/editor/api/status", "localhost")]
    [InlineData("/editor/api/status", "127.0.0.1")]
    [InlineData("/editor/api/status", "other")]
    [InlineData("/", "evil.example")]
    [InlineData("/editor/", "localhost")]
    [InlineData("/missing.js", "evil.example")]
    public async Task A_host_other_than_the_bound_loopback_address_is_refused_everywhere(string path, string hostKind)
    {
        // After a DNS rebinding the browser sends the attacker's name as Host. Refusing every Host but ours (even "localhost")
        // is what stops a rebound page from reaching the API, with or without a cookie.
        await using var rig = await ApiRig.StartAsync("none");
        var host = hostKind switch
        {
            "127.0.0.1" => "127.0.0.1",
            "other" => $"127.0.0.1:{rig.Port + 1}",
            _ => $"{hostKind}:{rig.Port}",
        };
        var request = rig.Build("GET", path);
        request.Headers.Host = host;

        var response = await rig.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var control = await rig.GetAsync(path);
        Assert.NotEqual(HttpStatusCode.BadRequest, control.StatusCode);
    }

    // ---- Cookie ----

    [Fact]
    public async Task Without_the_right_cookie_every_api_path_answers_401_with_no_body()
    {
        // The user's own browser has no cookie; a wrong guess must not reveal anything, not even which paths exist.
        await using var rig = await ApiRig.StartAsync("none");
        var sameLengthWrong = new string('A', rig.Secret.Value.Length);
        var cookies = new string?[] { null, "", "wrong", sameLengthWrong, rig.Secret.Value + "x", rig.Secret.Value[..^1] };
        foreach (var path in new[] { "/editor/api/status", "/editor/api/devices", "/editor/api/pairing", "/editor/api/nope", "/Editor/API/status" })
        {
            foreach (var cookie in cookies)
            {
                var request = rig.Build("GET", path);
                request.Headers.Remove("Cookie");
                if (cookie is not null)
                {
                    request.Headers.TryAddWithoutValidation("Cookie", EditorApi.CookieName + "=" + cookie);
                }

                var response = await rig.SendAsync(request);

                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Ct));
                Assert.False(response.Headers.Contains("WWW-Authenticate"));
            }
        }
    }

    [Fact]
    public async Task A_cookie_under_another_name_does_not_authenticate()
    {
        await using var rig = await ApiRig.StartAsync("none");
        var request = rig.Build("GET", "/editor/api/status");
        request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation("Cookie", "other=" + rig.Secret.Value);

        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task The_right_cookie_works_and_unknown_paths_then_answer_404()
    {
        await using var rig = await ApiRig.StartAsync("none");

        Assert.Equal(HttpStatusCode.OK, (await rig.GetAsync("/editor/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.GetAsync("/editor/api/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.GetAsync("/editor/api/status/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.GetAsync("/editor/api")).StatusCode);
        // Case-insensitive mapping applies the gate to this spelling too (a 401 without the cookie is tested above).
        Assert.Equal(HttpStatusCode.OK, (await rig.GetAsync("/Editor/API/status")).StatusCode);
    }

    [Fact]
    public async Task The_secret_appears_in_no_response_body_or_header()
    {
        // The cookie is set by the app through WebView2, never by the server; echoing it anywhere would defeat HttpOnly.
        await using var rig = await ApiRig.StartAsync();
        var token = rig.Fx.Registry.Add("Phone", LanFixture.TemplateId, false).Token;
        await rig.Lan.Pairing.StartAsync(null);
        rig.Lan.Pairing.ShowCode();
        var paths = new[] { "/editor/api/status", "/editor/api/devices", "/editor/api/templates", "/editor/api/pairing", "/editor/api/nope", "/", "/editor/" };
        try
        {
            foreach (var path in paths)
            {
                var response = await rig.GetAsync(path);
                var all = Everything(response, await response.Content.ReadAsStringAsync(Ct));
                Assert.DoesNotContain(rig.Secret.Value, all, StringComparison.Ordinal);
                Assert.False(response.Headers.Contains("Set-Cookie"), path);
            }

            foreach (var path in new[] { "/editor/api/pairing/code", "/editor/api/pairing/cancel" })
            {
                var response = await rig.PostAsync(path);
                var all = Everything(response, await response.Content.ReadAsStringAsync(Ct));
                Assert.DoesNotContain(rig.Secret.Value, all, StringComparison.Ordinal);
                Assert.False(response.Headers.Contains("Set-Cookie"), path);
            }
        }
        finally
        {
            await rig.Lan.Pairing.CancelAsync();
        }

        Assert.NotEmpty(token); // the device token never shows up either (devices.json holds only its hash)
        var devices = await (await rig.GetAsync("/editor/api/devices")).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(token, devices, StringComparison.Ordinal);
    }

    // ---- POST rules: Origin, Content-Type ----

    [Fact]
    public async Task A_post_with_a_wrong_content_type_is_415()
    {
        // text/plain is what a cross-site form can send; JSON-only closes that door even if the cookie were sent.
        await using var rig = await ApiRig.StartAsync("none");
        foreach (var type in new[] { "text/plain", "application/x-www-form-urlencoded", "application/jsonx", "multipart/form-data", "application/json; charset=utf-16" })
        {
            var request = rig.Build("POST", "/editor/api/pairing/cancel");
            request.Content = new StringContent("{}", Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await rig.SendAsync(request)).StatusCode);
        }

        var none = rig.Build("POST", "/editor/api/pairing/cancel");
        none.Content!.Headers.ContentType = null;
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await rig.SendAsync(none)).StatusCode);
    }

    [Fact]
    public async Task A_json_content_type_with_parameters_and_other_casing_is_accepted()
    {
        await using var rig = await ApiRig.StartAsync("none");
        var request = rig.Build("POST", "/editor/api/pairing/cancel");
        request.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse("Application/JSON; charset=UTF-8");

        // 503: the request got past every gate and reached the route (no LAN side here).
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rig.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_post_without_a_matching_origin_is_403()
    {
        // The Origin header is what a browser sets and a page cannot forge: a request from another site carries its own.
        await using var rig = await ApiRig.StartAsync("none");
        var missing = rig.Build("POST", "/editor/api/pairing/cancel");
        missing.Headers.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.SendAsync(missing)).StatusCode);

        foreach (var origin in new[] { "http://evil.example", "null", "", "http://localhost:" + rig.Port, $"http://127.0.0.1:{rig.Port + 1}", rig.Origin + "/", "https://127.0.0.1:" + rig.Port, rig.Origin.ToUpperInvariant() })
        {
            var request = rig.Build("POST", "/editor/api/pairing/cancel");
            request.Headers.Remove("Origin");
            request.Headers.TryAddWithoutValidation("Origin", origin);
            Assert.Equal(HttpStatusCode.Forbidden, (await rig.SendAsync(request)).StatusCode);
        }

        var two = rig.Build("POST", "/editor/api/pairing/cancel");
        two.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.SendAsync(two)).StatusCode); // two Origin values: never accepted

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rig.PostAsync("/editor/api/pairing/cancel")).StatusCode); // the right Origin gets through
    }

    [Fact]
    public async Task A_get_needs_no_origin_and_a_wrong_method_is_405()
    {
        await using var rig = await ApiRig.StartAsync("none");

        Assert.Equal(HttpStatusCode.OK, (await rig.GetAsync("/editor/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.PostAsync("/editor/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.GetAsync("/editor/api/pairing/start")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.GetAsync("/editor/api/devices/abc/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.SendAsync(rig.Build("PUT", "/editor/api/status"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.SendAsync(rig.Build("DELETE", "/editor/api/devices"))).StatusCode);
    }

    // ---- CORS ----

    [Fact]
    public async Task No_response_carries_cors_headers_and_options_is_405()
    {
        // No page of another origin may read a response. Without Access-Control-Allow-* the browser blocks that.
        await using var rig = await ApiRig.StartAsync("none");
        var requests = new List<HttpRequestMessage>
        {
            rig.Build("GET", "/editor/api/status"),
            rig.Build("GET", "/editor/api/nope"),
            rig.Build("GET", "/"),
            rig.Build("GET", "/editor/"),
            rig.Build("POST", "/editor/api/pairing/cancel"),
        };
        var withForeignOrigin = rig.Build("GET", "/editor/api/status");
        withForeignOrigin.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        requests.Add(withForeignOrigin);
        var noCookie = rig.Build("GET", "/editor/api/status");
        noCookie.Headers.Remove("Cookie");
        noCookie.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        requests.Add(noCookie);

        foreach (var request in requests)
        {
            var response = await rig.SendAsync(request);
            Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers), h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        }

        foreach (var path in new[] { "/editor/api/status", "/editor/api/pairing/start", "/", "/editor/", "/nope" })
        {
            foreach (var withCookie in new[] { true, false })
            {
                var preflight = rig.Build("OPTIONS", path);
                preflight.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
                preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
                if (!withCookie)
                {
                    preflight.Headers.Remove("Cookie");
                }

                var response = await rig.SendAsync(preflight);

                Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
                Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task Static_files_stay_served_without_a_cookie_but_nothing_secret_is_in_them()
    {
        await using var rig = await ApiRig.StartAsync("none");
        var request = rig.Build("GET", "/editor/");
        request.Headers.Remove("Cookie");

        var response = await rig.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("EDITOR-MARKER", await response.Content.ReadAsStringAsync(Ct));
    }

    // ---- Caching ----

    [Fact]
    public async Task Every_api_response_is_no_store_including_errors()
    {
        // The pairing status holds the code; the browser must not keep it in any cache.
        await using var rig = await ApiRig.StartAsync();
        var noCookie = rig.Build("GET", "/editor/api/pairing");
        noCookie.Headers.Remove("Cookie");
        var noOrigin = rig.Build("POST", "/editor/api/pairing/code");
        noOrigin.Headers.Remove("Origin");
        var responses = new[]
        {
            await rig.GetAsync("/editor/api/status"),
            await rig.GetAsync("/editor/api/devices"),
            await rig.GetAsync("/editor/api/templates"),
            await rig.GetAsync("/editor/api/pairing"),
            await rig.GetAsync("/editor/api/nope"),
            await rig.SendAsync(noCookie),
            await rig.SendAsync(noOrigin),
            await rig.PostAsync("/editor/api/devices/unknown/revoke"),
        };

        foreach (var response in responses)
        {
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }
    }

    // ---- Routes ----

    [Fact]
    public async Task Status_and_templates_are_served()
    {
        await using var rig = await ApiRig.StartAsync("none");
        rig.Status = new HostStatus { MdnsName = LanFixture.Label, LanServer = LanServerState.PortInUse, PortOwner = "other.exe", Addresses = ["192.168.77.10"] };

        var status = await ApiRig.JsonAsync(await rig.GetAsync("/editor/api/status"));
        var templates = await ApiRig.JsonAsync(await rig.GetAsync("/editor/api/templates"));

        Assert.Equal("portInUse", status.GetProperty("lanServer").GetString());
        Assert.Equal("other.exe", status.GetProperty("portOwner").GetString());
        Assert.Equal(LanFixture.TemplateId, templates.GetProperty("templates")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Devices_list_shows_the_connected_flag_from_the_live_connections()
    {
        // The editor must show which phones are online right now, not what devices.json says.
        await using var rig = await ApiRig.StartAsync();
        var (online, token) = rig.Fx.Registry.Add("Online phone", LanFixture.TemplateId, false);
        var offline = rig.Fx.Registry.Add("Offline phone", LanFixture.TemplateId, true).Device;
        using var socket = await rig.Lan.ConnectAuthenticatedAsync(token);
        await LanRig.WaitForAsync(() => rig.Lan.Connections.IsConnected(online.Id), v => v);

        var list = await ApiRig.JsonAsync(await rig.GetAsync("/editor/api/devices"));

        var devices = list.GetProperty("devices").EnumerateArray().ToDictionary(d => d.GetProperty("id").GetString()!);
        Assert.True(devices[online.Id].GetProperty("connected").GetBoolean());
        Assert.False(devices[offline.Id].GetProperty("connected").GetBoolean());
        Assert.True(devices[offline.Id].GetProperty("screenMismatch").GetBoolean());
        Assert.Equal("Online phone", devices[online.Id].GetProperty("name").GetString());
        Assert.Equal(LanFixture.TemplateId, devices[online.Id].GetProperty("templateId").GetString());
    }

    [Fact]
    public async Task Rename_changes_the_name_and_rejects_bad_input()
    {
        await using var rig = await ApiRig.StartAsync();
        var device = rig.Fx.Registry.Add("Old", LanFixture.TemplateId, false).Device;
        var path = $"/editor/api/devices/{device.Id}/rename";

        var ok = await rig.PostAsync(path, """{"name":"New name"}""");
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal("New name", rig.Fx.Registry.Devices.Single().Name);

        foreach (var bad in new[]
        {
            "{\"name\":\"a\\u0001b\"}",    // control character
            "{\"name\":\"\"}",             // empty
            "{\"name\":\"" + new string('x', 65) + "\"}", // too long
            "{}",                          // missing name
            """{"name":"x","extra":1}""",  // unknown field
            """{"name":5}""",              // wrong type
            "not json",
            "null",
            string.Empty,
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await rig.PostAsync(path, bad)).StatusCode);
        }

        Assert.Equal("New name", rig.Fx.Registry.Devices.Single().Name); // nothing bad got through
        Assert.Equal(HttpStatusCode.NotFound, (await rig.PostAsync("/editor/api/devices/unknown-id/rename", """{"name":"x"}""")).StatusCode);
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("-leading")]
    [InlineData("has%20space")]
    [InlineData("a%2Fb")]
    [InlineData("..")]
    public async Task A_device_id_that_is_not_an_id_is_404(string id)
    {
        await using var rig = await ApiRig.StartAsync();
        rig.Fx.Registry.Add("Phone", LanFixture.TemplateId, false);

        Assert.Equal(HttpStatusCode.NotFound, (await rig.PostAsync($"/editor/api/devices/{id}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.PostAsync($"/editor/api/devices/{id}/rename", """{"name":"x"}""")).StatusCode);
        Assert.Single(rig.Fx.Registry.Devices);
    }

    [Fact]
    public async Task Revoke_removes_the_device_and_drops_its_connection()
    {
        // Revoking must take effect at once: the phone that was just removed must not stay connected.
        await using var rig = await ApiRig.StartAsync();
        var (device, token) = rig.Fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var other = rig.Fx.Registry.Add("Other", LanFixture.TemplateId, false).Device;
        using var socket = await rig.Lan.ConnectAuthenticatedAsync(token);
        await LanRig.WaitForAsync(() => rig.Lan.Connections.IsConnected(device.Id), v => v);

        var response = await rig.PostAsync($"/editor/api/devices/{device.Id}/revoke");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await LanRig.ReadCloseCodeAsync(socket); // the host closed the phone's socket (throws on timeout)
        await LanRig.WaitForAsync(() => rig.Lan.Connections.IsConnected(device.Id), v => !v);
        var list = await ApiRig.JsonAsync(await rig.GetAsync("/editor/api/devices"));
        Assert.Equal([other.Id], list.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("id").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await rig.PostAsync($"/editor/api/devices/{device.Id}/revoke")).StatusCode);
    }

    // With 443 taken (or no CA) the phone could download the CA but "Continue" would lead to no server: refuse to start.
    [Theory]
    [InlineData(LanServerState.PortInUse)]
    [InlineData(LanServerState.Starting)]
    [InlineData(LanServerState.CaUnavailable)]
    public async Task Pairing_does_not_start_without_a_running_https_server(LanServerState state)
    {
        await using var rig = await ApiRig.StartAsync();
        rig.Status = new HostStatus { MdnsName = LanFixture.Label, LanServer = state, Addresses = ["192.168.77.10"] };

        var response = await rig.PostAsync("/editor/api/pairing/start", $$"""{"templateId":"{{LanFixture.TemplateId}}"}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("closed", (await ApiRig.JsonAsync(await rig.GetAsync("/editor/api/pairing"))).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Pairing_start_code_and_cancel_follow_the_session()
    {
        await using var rig = await ApiRig.StartAsync();

        var closed = await ApiRig.JsonAsync(await rig.GetAsync("/editor/api/pairing"));
        var started = await ApiRig.JsonAsync(await rig.PostAsync("/editor/api/pairing/start", $$"""{"templateId":"{{LanFixture.TemplateId}}"}"""));
        var withCode = await ApiRig.JsonAsync(await rig.PostAsync("/editor/api/pairing/code"));
        var polled = await ApiRig.JsonAsync(await rig.GetAsync("/editor/api/pairing"));
        var cancelled = await ApiRig.JsonAsync(await rig.PostAsync("/editor/api/pairing/cancel"));

        Assert.Equal("closed", closed.GetProperty("state").GetString());
        Assert.Equal("caDownload", started.GetProperty("state").GetString());
        Assert.Equal("codeShown", withCode.GetProperty("state").GetString());
        Assert.Matches(@"\A[0-9]{6}\z", withCode.GetProperty("code").GetString()!);
        Assert.Equal(withCode.GetProperty("code").GetString(), polled.GetProperty("code").GetString());
        Assert.Equal("closed", cancelled.GetProperty("state").GetString());
        Assert.False(cancelled.TryGetProperty("code", out _));
    }

    [Fact]
    public async Task Pairing_start_accepts_an_empty_object_for_a_device_that_is_not_listed()
    {
        await using var rig = await ApiRig.StartAsync();

        var started = await ApiRig.JsonAsync(await rig.PostAsync("/editor/api/pairing/start", "{}"));

        Assert.Equal("caDownload", started.GetProperty("state").GetString());
        await rig.PostAsync("/editor/api/pairing/cancel");
    }

    [Fact]
    public async Task Pairing_start_rejects_bad_input_with_400()
    {
        await using var rig = await ApiRig.StartAsync();
        foreach (var bad in new[]
        {
            """{"templateId":"not-listed"}""",   // valid id, unknown template
            """{"templateId":"UPPER"}""",        // not an id
            """{"templateId":5}""",
            """{"other":1}""",
            "[]",
            "garbage",
            string.Empty,
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await rig.PostAsync("/editor/api/pairing/start", bad)).StatusCode);
        }

        Assert.Equal(PairingState.Closed, rig.Lan.Pairing.Status.State); // nothing was started
    }

    [Fact]
    public async Task Showing_a_code_in_the_wrong_state_is_409()
    {
        await using var rig = await ApiRig.StartAsync();

        var response = await rig.PostAsync("/editor/api/pairing/code"); // wizard not open

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Pairing_is_503_without_a_lan_side_but_devices_and_templates_still_work()
    {
        // The LAN side may be down (no CA, devices unavailable). The editor must still show what it can.
        await using var rig = await ApiRig.StartAsync("none");
        rig.Fx.Registry.Add("Phone", LanFixture.TemplateId, false);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rig.GetAsync("/editor/api/pairing")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rig.PostAsync("/editor/api/pairing/start")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rig.PostAsync("/editor/api/pairing/code")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rig.PostAsync("/editor/api/pairing/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.GetAsync("/editor/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.GetAsync("/editor/api/templates")).StatusCode);
        var list = await ApiRig.JsonAsync(await rig.GetAsync("/editor/api/devices"));
        Assert.False(list.GetProperty("devices")[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task Starting_pairing_without_a_lan_address_is_503()
    {
        await using var rig = await ApiRig.StartAsync("noaddress");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rig.PostAsync("/editor/api/pairing/start")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.GetAsync("/editor/api/pairing")).StatusCode);
    }

    [Fact]
    public async Task Without_a_loaded_registry_the_device_routes_are_503_and_nothing_is_replaced()
    {
        await using var rig = await ApiRig.StartAsync("none", registryLoaded: false);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rig.GetAsync("/editor/api/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rig.PostAsync("/editor/api/devices/abc/rename", """{"name":"x"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rig.PostAsync("/editor/api/devices/abc/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.GetAsync("/editor/api/templates")).StatusCode);
    }

    // ---- Body size ----

    [Fact]
    public async Task A_body_over_4_KiB_is_413_whether_or_not_it_declares_its_length()
    {
        // Every route reads its body; an unbounded read would let any local process hold memory.
        await using var rig = await ApiRig.StartAsync();
        var big = "{\"name\":\"x\",\"pad\":\"" + new string('a', 5000) + "\"}";

        var declared = await rig.PostAsync("/editor/api/devices/abc/rename", big);

        var chunkedRequest = rig.Build("POST", "/editor/api/pairing/cancel");
        chunkedRequest.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(big)));
        chunkedRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        chunkedRequest.Headers.TransferEncodingChunked = true;
        var chunked = await rig.SendAsync(chunkedRequest);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        Assert.Equal(PairingState.Closed, rig.Lan.Pairing.Status.State);
    }

    [Fact]
    public async Task A_body_of_exactly_4_KiB_is_still_read()
    {
        await using var rig = await ApiRig.StartAsync();
        var device = rig.Fx.Registry.Add("Phone", LanFixture.TemplateId, false).Device;
        var body = """{"name":"ok"}""" + new string(' ', ApiLimits - 13);

        var response = await rig.PostAsync($"/editor/api/devices/{device.Id}/rename", body);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static int ApiLimits => EditorApi.MaxBodyBytes;

    [Fact]
    public async Task The_api_does_not_exist_on_the_loopback_server_without_options()
    {
        // The default (as the old tests start it) maps no API at all.
        await using var fx = new LanFixture();
        await using var server = await LocalServer.StartAsync(fx.ClientDir, fx.ShippedDir);
        using var http = new HttpClient();

        var response = await http.GetAsync(new Uri(server.BaseAddress, "/editor/api/status"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
