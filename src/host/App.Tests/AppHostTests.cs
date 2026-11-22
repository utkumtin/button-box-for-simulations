using System.Net;
using ButtonBox.App.Lan;
using ButtonBox.Core.Contracts;
using ButtonBox.Discovery;
using ButtonBox.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ButtonBox.App.Tests;

public sealed class AppHostTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-host-" + Guid.NewGuid().ToString("N"));
    private readonly string _keyName = "bb-test-" + Guid.NewGuid().ToString("N");
    private readonly List<NetworkWatcher> _watchers = [];
    private readonly List<AppHost> _hosts = [];
    private int _httpsPort;

    private sealed class NoSignals : INetworkChangeSource
    {
        public event Action? Signaled
        {
            add { }
            remove { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        CertificateAuthority.DeleteServerKey(_keyName);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder.
        }
    }

    private string DataDir => Path.Combine(_root, "data");

    private string SettingsPath => Path.Combine(DataDir, "settings.json");

    private string DevicesPath => Path.Combine(DataDir, "devices.json");

    private AppHost Create(bool withLan = true)
    {
        var client = Path.Combine(_root, "client");
        var shipped = Path.Combine(_root, "shipped");
        Directory.CreateDirectory(client);
        Directory.CreateDirectory(shipped);
        File.WriteAllText(Path.Combine(client, "index.html"), "x");
        var watcher = new NetworkWatcher(
            () => withLan ? [new LanAddress(1, LanFixture.FakeLan, 24)] : [],
            new NoSignals(), new FakeTimeProvider(), TimeSpan.FromSeconds(2));
        _watchers.Add(watcher);
        var host = AppHost.Create(new AppHostOptions
        {
            DataDir = DataDir,
            ShippedTemplatesDir = shipped,
            ClientDir = client,
            LogoPng = LanFixture.MakeLogo(),
            Network = watcher,
            ServerKeyName = _keyName,
            HttpsPort = _httpsPort = LanFixture.FreePort(),
            PairingPort = LanFixture.FreePort(),
            PairingFallbackPort = LanFixture.FreePort(),
            MdnsPort = LanFixture.FreePort(),
            BindAddress = _ => IPAddress.Loopback,
        });
        _hosts.Add(host);
        return host;
    }

    // ---- settings ----

    [Fact]
    public void First_run_creates_a_valid_name_and_the_second_run_keeps_it()
    {
        // The name is the phones' home-screen address: it must be generated once and never change.
        var first = AppSettings.LoadOrCreate(DataDir);
        var second = AppSettings.LoadOrCreate(DataDir);

        Assert.Null(first.Problem);
        Assert.True(MdnsName.IsValid(first.MdnsName));
        Assert.Equal(first.MdnsName, second.MdnsName);
        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains(first.MdnsName!, json);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":1,"mdnsName":"buttonbox-zzzz"}""")]
    [InlineData("""{"schemaVersion":1,"mdnsName":"buttonbox-ABCD"}""")]
    [InlineData("""{"schemaVersion":1,"mdnsName":null}""")]
    [InlineData("""{"schemaVersion":2,"mdnsName":"buttonbox-abcd"}""")]
    [InlineData("""{"mdnsName":"buttonbox-abcd"}""")]
    [InlineData("""{"schemaVersion":1,"mdnsName":"buttonbox-abcd","extra":true}""")]
    [InlineData("[]")]
    public void An_invalid_settings_file_is_reported_and_never_overwritten(string content)
    {
        // Overwriting would give the PC a new name and strand every paired phone.
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, content);
        var before = File.ReadAllBytes(SettingsPath);

        var result = AppSettings.LoadOrCreate(DataDir);

        Assert.Null(result.MdnsName);
        Assert.NotNull(result.Problem);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.Equal([SettingsPath], Directory.GetFiles(DataDir)); // no temp file left either
    }

    [Fact]
    public void An_oversized_settings_file_is_invalid()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, """{"schemaVersion":1,"mdnsName":"buttonbox-abcd"}""" + new string(' ', 5000));

        Assert.NotNull(AppSettings.LoadOrCreate(DataDir).Problem);
    }

    // ---- composition failures ----

    [Fact]
    public async Task Invalid_settings_means_the_lan_side_does_not_start_and_nothing_is_created()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, "{ broken");
        var host = Create();

        await host.StartAsync();

        Assert.Equal(SetupProblem.SettingsUnavailable, host.Setup);
        Assert.Equal(LanServerState.StartFailed, host.Status.LanServer);
        Assert.Null(host.Lan);
        Assert.False(Directory.Exists(Path.Combine(DataDir, "ca"))); // no CA was generated behind the user's back
        Assert.NotNull(host.Registry); // the devices page still works
        Assert.NotNull(StatusMessages.For(host.Status));
    }

    [Fact]
    public async Task A_damaged_registry_means_devicesUnavailable_and_the_file_is_untouched()
    {
        // An empty registry in its place would silently un-pair every phone on the next write.
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(DevicesPath, """{"schemaVersion":1,"devices":[{"id":"BAD"}]}""");
        var before = File.ReadAllBytes(DevicesPath);
        var host = Create();

        await host.StartAsync();

        Assert.Equal(SetupProblem.DevicesUnavailable, host.Setup);
        Assert.Null(host.Registry);
        Assert.Null(host.Lan);
        var status = host.Status;
        Assert.Equal(LanServerState.DevicesUnavailable, status.LanServer);
        Assert.True(MdnsName.IsValid(status.MdnsName)); // settings were fine, so the name is known
        Assert.Equal(before, File.ReadAllBytes(DevicesPath));
        Assert.False(Directory.Exists(Path.Combine(DataDir, "ca")));
        Assert.Contains("devices.json", StatusMessages.For(status));
    }

    [Fact]
    public async Task A_damaged_registry_with_damaged_settings_reports_the_settings_first_and_changes_neither()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, "x");
        File.WriteAllText(DevicesPath, "y");
        var host = Create();

        await host.StartAsync();

        Assert.Equal(LanServerState.StartFailed, host.Status.LanServer);
        Assert.Equal("x", File.ReadAllText(SettingsPath));
        Assert.Equal("y", File.ReadAllText(DevicesPath));
    }

    // ---- the LAN side through the host ----

    [Fact]
    public async Task A_healthy_start_runs_the_lan_server_under_the_generated_name()
    {
        var host = Create();
        var changes = 0;
        host.StatusChanged += () => Interlocked.Increment(ref changes);

        await host.StartAsync();

        var status = host.Status;
        Assert.Equal(LanServerState.Running, status.LanServer);
        Assert.Equal(AppSettings.LoadOrCreate(DataDir).MdnsName, status.MdnsName);
        Assert.Equal(["192.168.77.10"], status.Addresses);
        Assert.NotNull(host.Lan!.Pairing);
        Assert.True(changes > 0);
        Assert.Null(StatusMessages.For(status));
        Assert.True(File.Exists(Path.Combine(DataDir, "ca", "ca.cer")));
    }

    [Fact]
    public async Task Starting_twice_does_not_start_a_second_lan_host()
    {
        var host = Create();
        await host.StartAsync();
        var lan = host.Lan;

        await host.StartAsync();

        Assert.Same(lan, host.Lan);
    }

    [Fact]
    public async Task Without_a_lan_address_the_state_is_noLanAddress()
    {
        var host = Create(withLan: false);

        await host.StartAsync();

        Assert.Equal(LanServerState.NoLanAddress, host.Status.LanServer);
        Assert.Empty(host.Status.Addresses);
        Assert.Null(StatusMessages.For(host.Status));
    }

    [Fact]
    public async Task A_lone_ca_certificate_maps_to_caUnavailable_and_is_not_repaired()
    {
        // The state is also what the tray balloon is keyed on; a regenerated CA would orphan every paired phone.
        Directory.CreateDirectory(Path.Combine(DataDir, "ca"));
        File.WriteAllText(Path.Combine(DataDir, "ca", "ca.cer"), "garbage");
        var host = Create();

        await host.StartAsync();

        Assert.Equal(LanServerState.CaUnavailable, host.Status.LanServer);
        Assert.Null(host.Lan!.Pairing);
        Assert.Equal(["ca.cer"], Directory.GetFiles(Path.Combine(DataDir, "ca")).Select(Path.GetFileName));
        Assert.NotNull(StatusMessages.For(host.Status));
    }

    [Fact]
    public async Task A_taken_https_port_is_reported_with_its_owner_in_the_message()
    {
        var port = LanFixture.FreePort();
        var client = Path.Combine(_root, "client");
        using var occupier = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        occupier.Start();
        Directory.CreateDirectory(client);
        File.WriteAllText(Path.Combine(client, "index.html"), "x");
        var watcher = new NetworkWatcher(() => [new LanAddress(1, LanFixture.FakeLan, 24)], new NoSignals(), new FakeTimeProvider(), TimeSpan.FromSeconds(2));
        _watchers.Add(watcher);
        var host = AppHost.Create(new AppHostOptions
        {
            DataDir = DataDir,
            ShippedTemplatesDir = Path.Combine(_root, "shipped"),
            ClientDir = client,
            LogoPng = LanFixture.MakeLogo(),
            Network = watcher,
            ServerKeyName = _keyName,
            HttpsPort = port,
            PairingPort = LanFixture.FreePort(),
            PairingFallbackPort = LanFixture.FreePort(),
            MdnsPort = LanFixture.FreePort(),
            BindAddress = _ => IPAddress.Loopback,
        });
        _hosts.Add(host);

        await host.StartAsync();

        Assert.Equal(LanServerState.PortInUse, host.Status.LanServer);
        Assert.Contains("443", StatusMessages.For(host.Status));
    }

    [Fact]
    public void The_port_message_names_the_owner_and_copes_with_an_unknown_one()
    {
        var named = StatusMessages.For(new HostStatus { MdnsName = "x", LanServer = LanServerState.PortInUse, PortOwner = "skype.exe", Addresses = [] });
        var unnamed = StatusMessages.For(new HostStatus { MdnsName = "x", LanServer = LanServerState.PortInUse, Addresses = [] });

        Assert.Contains("skype.exe", named);
        Assert.Contains("another program", unnamed);
        foreach (var state in new[] { LanServerState.Starting, LanServerState.Running, LanServerState.NoLanAddress })
        {
            Assert.Null(StatusMessages.For(new HostStatus { MdnsName = "x", LanServer = state, Addresses = [] }));
        }
    }

    [Fact]
    public async Task Disposing_during_a_start_leaves_nothing_running()
    {
        var host = Create();

        _ = host.StartAsync();
        await host.DisposeAsync();

        // A quit in the middle of the start must not leave a listener behind: the next launch needs the port.
        using var client = new System.Net.Sockets.TcpClient();
        await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(
            async () => await client.ConnectAsync(IPAddress.Loopback, _httpsPort, LanFixture.Ct));
    }

    [Fact]
    public void The_editor_secret_is_new_for_every_host()
    {
        var a = Create();
        var b = Create();

        Assert.NotEqual(a.Secret.Value, b.Secret.Value);
        Assert.False(a.Secret.Matches(b.Secret.Value));
    }
}
