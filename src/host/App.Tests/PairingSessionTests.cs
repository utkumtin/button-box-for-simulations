using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ButtonBox.App.Pairing;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;
using Xunit;

namespace ButtonBox.App.Tests;

public sealed class PairingSessionTests : IAsyncDisposable
{
    private readonly LanFixture _fx = new();
    private readonly List<TcpListener> _occupiers = [];
    private LanRig? _rig;
    private HttpClient? _http;

    private static CancellationToken Ct => LanFixture.Ct;

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        foreach (var l in _occupiers)
        {
            l.Stop();
        }

        if (_rig is not null)
        {
            await _rig.DisposeAsync();
        }

        await _fx.DisposeAsync();
    }

    private async Task<LanRig> StartAsync()
    {
        _rig = await LanRig.StartAsync(_fx);
        _http = _rig.CreateClient();
        return _rig;
    }

    private async Task<HttpResponseMessage> PostAsync(string json, string? mediaType = "application/json", bool withOrigin = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pair")
        {
            Content = mediaType is null ? new StringContent(json) : LanRig.Json(json, mediaType),
        };
        if (withOrigin)
        {
            request.Headers.Add("Origin", _rig!.DefaultOrigin);
        }

        return await _http!.SendAsync(request, Ct);
    }

    private async Task<int> PairAsync(string code, int width = 852, int height = 393, double pixelRatio = 3, int right = 59)
    {
        using var response = await PostAsync(LanRig.PairJson(code, width, height, pixelRatio, right));
        return (int)response.StatusCode;
    }

    private static string WrongCodeFor(string code) => code == "000000" ? "000001" : "000000";

    private TcpListener Occupy(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _occupiers.Add(listener);
        return listener;
    }

    [Fact]
    public async Task Pairing_is_404_while_closed()
    {
        await StartAsync();

        Assert.Equal(404, await PairAsync("123456"));
        Assert.Equal(PairingState.Closed, _rig!.Pairing.Status.State);
    }

    [Fact]
    public async Task Before_a_code_is_shown_every_attempt_is_403_and_consumes_nothing()
    {
        var rig = await StartAsync();
        await rig.Pairing.StartAsync(null);

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(403, await PairAsync("123456"));
        }

        var status = rig.Pairing.ShowCode();
        Assert.Equal(5, status.AttemptsLeft);
        Assert.Equal(PairingState.CodeShown, status.State);
    }

    [Fact]
    public async Task Start_reports_the_page_address_and_the_ca_fingerprint()
    {
        var rig = await StartAsync();

        var status = await rig.Pairing.StartAsync(null);

        Assert.Equal(PairingState.CaDownload, status.State);
        Assert.Equal($"http://{LanFixture.Label}.local:{rig.PrimaryPort}", status.Address); // not port 80, so the port is shown
        Assert.Equal(_fx.Ca.Sha256Fingerprint, status.CaFingerprint);
        Assert.Null(status.Code);
        using var page = new HttpClient();
        Assert.Contains("Download Certificate", await page.GetStringAsync($"http://127.0.0.1:{rig.PrimaryPort}/", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Four_wrong_codes_are_403_and_the_fifth_burns_the_code_for_the_session_with_410()
    {
        var rig = await StartAsync();
        await rig.Pairing.StartAsync(null);
        var code = rig.Pairing.ShowCode().Code!;
        var wrong = WrongCodeFor(code);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.Equal(403, await PairAsync(wrong));
            Assert.Equal(5 - attempt, rig.Pairing.Status.AttemptsLeft);
        }

        Assert.Equal(410, await PairAsync(wrong));
        var status = rig.Pairing.Status;
        Assert.Equal(PairingState.Failed, status.State);
        Assert.Equal(PairingFailure.CodeBurned, status.Failure);
        // Burned for everybody: the right code no longer works either.
        Assert.Equal(410, await PairAsync(code));
        Assert.Empty(_fx.Registry.Devices);

        // The wizard shows a new code and pairing works again.
        var next = rig.Pairing.ShowCode();
        Assert.Equal(PairingState.CodeShown, next.State);
        Assert.Equal(200, await PairAsync(next.Code!));
    }

    [Fact]
    public async Task An_expired_code_is_410_and_shows_as_expired_even_before_anyone_tries()
    {
        var rig = await StartAsync();
        await rig.Pairing.StartAsync(null);
        var code = rig.Pairing.ShowCode().Code!;

        _fx.Time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        Assert.Equal(PairingState.CodeShown, rig.Pairing.Status.State);
        _fx.Time.Advance(TimeSpan.FromSeconds(1));

        var status = rig.Pairing.Status;
        Assert.Equal(PairingState.Failed, status.State);
        Assert.Equal(PairingFailure.CodeExpired, status.Failure);
        Assert.Equal(410, await PairAsync(code));

        var fresh = rig.Pairing.ShowCode();
        Assert.Equal(200, await PairAsync(fresh.Code!));
    }

    [Fact]
    public async Task A_correct_code_pairs_returns_the_token_once_and_stops_the_http_page()
    {
        var rig = await StartAsync();
        await rig.Pairing.StartAsync(LanFixture.TemplateId);
        var code = rig.Pairing.ShowCode().Code!;

        using var response = await PostAsync(LanRig.PairJson(code));
        var body = await response.Content.ReadAsStringAsync(Ct);
        var paired = JsonSerializer.Deserialize<PairResponse>(body, ContractJson.Options)!;

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(paired.DeviceId, _fx.Registry.Authenticate(paired.Token)?.Id);
        var device = Assert.Single(_fx.Registry.Devices);
        Assert.Equal("iPhone 14 Pro", device.Name);
        Assert.Equal(LanFixture.TemplateId, device.TemplateId);
        Assert.False(device.ScreenMismatch);

        var status = rig.Pairing.Status;
        Assert.Equal(PairingState.Paired, status.State);
        Assert.Equal(paired.DeviceId, status.DeviceId);
        Assert.False(status.ScreenMismatch);
        Assert.Null(status.Code); // the token and the code are gone from the status
        using var page = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => page.GetAsync($"http://127.0.0.1:{rig.PrimaryPort}/", Ct));

        // The new token logs in.
        using var socket = await rig.ConnectAuthenticatedAsync(paired.Token);
        Assert.True(rig.Connections.IsConnected(paired.DeviceId));

        // The code is single use.
        Assert.Equal(410, await PairAsync(code));
        Assert.Single(_fx.Registry.Devices);
    }

    [Fact]
    public async Task A_phone_with_another_screen_than_the_chosen_model_is_flagged_but_still_pairs()
    {
        var rig = await StartAsync();
        await rig.Pairing.StartAsync(LanFixture.TemplateId);
        var code = rig.Pairing.ShowCode().Code!;

        Assert.Equal(200, await PairAsync(code, width: 844, height: 390, pixelRatio: 3));

        Assert.True(rig.Pairing.Status.ScreenMismatch);
        Assert.True(Assert.Single(_fx.Registry.Devices).ScreenMismatch);
    }

    [Fact]
    public async Task Different_insets_alone_are_not_a_mismatch()
    {
        var rig = await StartAsync();
        await rig.Pairing.StartAsync(LanFixture.TemplateId);
        var code = rig.Pairing.ShowCode().Code!;

        Assert.Equal(200, await PairAsync(code, right: 47));

        Assert.False(rig.Pairing.Status.ScreenMismatch);
    }

    [Fact]
    public async Task Without_a_chosen_model_a_generic_template_is_created_from_the_reported_screen()
    {
        var rig = await StartAsync();
        await rig.Pairing.StartAsync(null);
        var code = rig.Pairing.ShowCode().Code!;

        Assert.Equal(200, await PairAsync(code, width: 800, height: 360, pixelRatio: 2));

        var device = Assert.Single(_fx.Registry.Devices);
        Assert.Equal("Device", device.Name);
        Assert.StartsWith("generic-", device.TemplateId, StringComparison.Ordinal);
        Assert.True(_fx.Templates.TryGet(device.TemplateId, out var template));
        Assert.Equal("Generic 800×360", template.Name);
        Assert.False(device.ScreenMismatch);
        Assert.Single(Directory.GetFiles(_fx.GenericDir));
    }

    [Theory]
    [InlineData("""{"code":"123456","screen":{"width":852,"height":393,"safeArea":{"top":0,"right":59,"bottom":20,"left":59},"pixelRatio":9}}""")]
    [InlineData("""{"code":"123456","screen":{"width":852,"height":393,"safeArea":{"top":0,"right":59,"bottom":20,"left":59},"pixelRatio":3},"extra":1}""")]
    [InlineData("""{"code":123456,"screen":{"width":852,"height":393,"safeArea":{"top":0,"right":59,"bottom":20,"left":59},"pixelRatio":3}}""")]
    [InlineData("""{"code":"12345","screen":{"width":852,"height":393,"safeArea":{"top":0,"right":59,"bottom":20,"left":59},"pixelRatio":3}}""")]
    [InlineData("""{"code":"123456"}""")]
    [InlineData("""{"code":"123456","screen":{"width":852,"height":10,"safeArea":{"top":0,"right":59,"bottom":20,"left":59},"pixelRatio":3}}""")]
    [InlineData("null")]
    [InlineData("nonsense")]
    [InlineData("")]
    public async Task An_invalid_request_is_400_and_does_not_count_as_an_attempt(string json)
    {
        var rig = await StartAsync();
        await rig.Pairing.StartAsync(null);
        rig.Pairing.ShowCode();

        using var response = await PostAsync(json);

        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal(5, rig.Pairing.Status.AttemptsLeft);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/json-patch+json")]
    public async Task A_content_type_other_than_json_is_415(string mediaType)
    {
        await StartAsync();

        using var response = await PostAsync(LanRig.PairJson("123456"), mediaType);

        Assert.Equal(415, (int)response.StatusCode);
    }

    [Fact]
    public async Task A_body_over_2_KiB_is_413()
    {
        await StartAsync();

        using var response = await PostAsync(LanRig.PairJson("123456") + new string(' ', 2100));

        Assert.Equal(413, (int)response.StatusCode);
    }

    [Fact]
    public async Task More_than_ten_attempts_in_ten_seconds_get_429_regardless_of_who_sends_them()
    {
        // The code has 6 digits; the global limit is the second line of defence behind the 5-attempt lockout.
        await StartAsync();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(404, await PairAsync("123456"));
        }

        Assert.Equal(429, await PairAsync("123456"));
        _fx.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(404, await PairAsync("123456"));
    }

    [Fact]
    public async Task Cancel_closes_the_session_and_the_page_and_is_idempotent()
    {
        var rig = await StartAsync();
        await rig.Pairing.StartAsync(null);
        var code = rig.Pairing.ShowCode().Code!;

        Assert.Equal(PairingState.Closed, (await rig.Pairing.CancelAsync()).State);
        Assert.Equal(PairingState.Closed, (await rig.Pairing.CancelAsync()).State);

        Assert.Equal(404, await PairAsync(code));
        using var page = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => page.GetAsync($"http://127.0.0.1:{rig.PrimaryPort}/", Ct));
        Assert.Empty(_fx.Registry.Devices);
    }

    [Fact]
    public async Task Showing_a_code_is_refused_when_the_session_is_closed()
    {
        var rig = await StartAsync();

        Assert.Throws<InvalidOperationException>(() => rig.Pairing.ShowCode());
        await rig.Pairing.StartAsync(null);
        await rig.Pairing.CancelAsync();
        Assert.Throws<InvalidOperationException>(() => rig.Pairing.ShowCode());
    }

    [Fact]
    public async Task An_unknown_template_is_refused_and_opens_nothing()
    {
        var rig = await StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Pairing.StartAsync("no-such-phone"));

        Assert.Equal(PairingState.Closed, rig.Pairing.Status.State);
    }

    [Fact]
    public async Task Starting_again_restarts_cleanly_and_forgets_the_old_code()
    {
        var rig = await StartAsync();
        await rig.Pairing.StartAsync(null);
        var oldCode = rig.Pairing.ShowCode().Code!;

        var status = await rig.Pairing.StartAsync(LanFixture.TemplateId);

        Assert.Equal(PairingState.CaDownload, status.State);
        Assert.Null(status.Code);
        Assert.Equal(403, await PairAsync(oldCode)); // no code shown in the new session
        using var page = new HttpClient();
        Assert.Contains("Download", await page.GetStringAsync($"http://127.0.0.1:{rig.PrimaryPort}/", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_idle_wizard_closes_itself_after_10_minutes_and_every_step_postpones_it()
    {
        var rig = await StartAsync();
        await rig.Pairing.StartAsync(null);

        _fx.Time.Advance(TimeSpan.FromMinutes(9));
        rig.Pairing.ShowCode();
        _fx.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.NotEqual(PairingState.Closed, rig.Pairing.Status.State);

        _fx.Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));

        await LanRig.WaitForAsync(() => rig.Pairing.Status.State, state => state == PairingState.Closed);
        using var page = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => page.GetAsync($"http://127.0.0.1:{rig.PrimaryPort}/", Ct));
    }

    [Fact]
    public async Task When_the_primary_port_is_taken_the_fallback_port_is_used_and_shown()
    {
        var rig = await StartAsync();
        Occupy(rig.PrimaryPort);

        var status = await rig.Pairing.StartAsync(null);

        Assert.Equal(PairingState.CaDownload, status.State);
        Assert.Equal($"http://{LanFixture.Label}.local:{rig.FallbackPort}", status.Address);
        using var page = new HttpClient();
        Assert.Contains("Download", await page.GetStringAsync($"http://127.0.0.1:{rig.FallbackPort}/", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_both_ports_are_taken_the_session_fails_with_port_in_use()
    {
        var rig = await StartAsync();
        Occupy(rig.PrimaryPort);
        Occupy(rig.FallbackPort);

        var status = await rig.Pairing.StartAsync(null);

        Assert.Equal(PairingState.Failed, status.State);
        Assert.Equal(PairingFailure.PortInUse, status.Failure);
        Assert.Null(status.Address);
        Assert.Throws<InvalidOperationException>(() => rig.Pairing.ShowCode());
        Assert.Equal(404, await PairAsync("123456"));
    }

    [Fact]
    public async Task A_full_registry_answers_409_and_the_wizard_must_show_a_new_code()
    {
        var rig = await StartAsync();
        for (var i = 0; i < DeviceRegistry.MaxDevices; i++)
        {
            _fx.Registry.Add("Phone " + i, LanFixture.TemplateId, false);
        }

        await rig.Pairing.StartAsync(null);
        var code = rig.Pairing.ShowCode().Code!;

        Assert.Equal(409, await PairAsync(code));
        Assert.Equal(PairingState.Failed, rig.Pairing.Status.State);
        Assert.Equal(DeviceRegistry.MaxDevices, _fx.Registry.Devices.Count);
    }

    [Fact]
    public async Task Without_a_lan_address_the_session_cannot_start()
    {
        await using var session = new PairingSession(new PairingSessionOptions
        {
            Authority = _fx.Ca,
            Registry = _fx.Registry,
            Templates = _fx.Templates,
            MdnsLabel = LanFixture.Label,
            BindAddresses = () => [],
            Time = _fx.Time,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(null));
        Assert.Equal(PairingState.Closed, session.Status.State);
    }

    [Fact]
    public async Task Changed_is_raised_on_every_step()
    {
        var rig = await StartAsync();
        var count = 0;
        rig.Pairing.Changed += () => Interlocked.Increment(ref count);

        await rig.Pairing.StartAsync(null);
        rig.Pairing.ShowCode();
        await rig.Pairing.CancelAsync();

        Assert.Equal(3, Volatile.Read(ref count));
    }
}
