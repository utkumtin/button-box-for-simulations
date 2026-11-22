using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using ButtonBox.App.Lan;
using ButtonBox.Core.Contracts;
using ButtonBox.Discovery;
using ButtonBox.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ButtonBox.App.Tests;

public sealed class LanHostTests : IAsyncDisposable
{
    private static readonly IPAddress SecondLan = IPAddress.Parse("192.168.77.22");

    private readonly LanFixture _fx = new();
    private readonly FakeTimeProvider _watcherTime = new();
    private readonly FakeSignals _signals = new();
    private readonly List<TcpListener> _occupiers = [];
    private readonly List<Socket> _udpOccupiers = [];
    private List<LanAddress> _addresses = [new LanAddress(1, LanFixture.FakeLan, 24)];
    private NetworkWatcher? _watcher;
    private LanHost? _host;
    private int _httpsPort;

    private static CancellationToken Ct => LanFixture.Ct;

    private sealed class FakeSignals : INetworkChangeSource
    {
        public event Action? Signaled;

        public void Raise() => Signaled?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        _watcher?.Dispose();
        foreach (var l in _occupiers)
        {
            l.Stop();
        }

        foreach (var s in _udpOccupiers)
        {
            s.Dispose();
        }

        await _fx.DisposeAsync();
    }

    private LanHost CreateHost(string? caDirectory = null, Func<CertificateAuthority, string, IReadOnlyList<IPAddress>, X509Certificate2>? issue = null, int? mdnsPort = null)
    {
        _watcher = new NetworkWatcher(() => _addresses, _signals, _watcherTime, TimeSpan.FromSeconds(2));
        _httpsPort = LanFixture.FreePort();
        return _host = new LanHost(new LanHostOptions
        {
            CaDirectory = caDirectory ?? Path.Combine(_fx.Root, "ca"),
            ServerKeyName = _fx.KeyName,
            ClientDir = _fx.ClientDir,
            LogoPng = LanFixture.MakeLogo(),
            MdnsLabel = LanFixture.Label,
            Registry = _fx.Registry,
            Templates = _fx.Templates,
            Network = _watcher,
            HttpsPort = _httpsPort,
            PairingPort = LanFixture.FreePort(),
            PairingFallbackPort = LanFixture.FreePort(),
            MdnsPort = mdnsPort ?? FreeUdpPort(),
            Time = _fx.Time,
            BindAddress = _ => IPAddress.Loopback,
            IssueCertificate = issue,
        });
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private void ChangeNetwork(params IPAddress[] addresses)
    {
        _addresses = [.. addresses.Select((a, i) => new LanAddress(i + 1, a, 24))];
        _signals.Raise();
        _watcherTime.Advance(TimeSpan.FromSeconds(2));
    }

    private HttpClient Client() => new(LanFixture.Handler(_fx.Ca.CertificateDer), disposeHandler: true)
    {
        BaseAddress = new Uri($"https://{LanFixture.Label}.local:{_httpsPort}/"),
    };

    private async Task<bool> IsPortRefusedAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, Ct);
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    [Fact]
    public async Task With_a_lan_address_the_server_runs_and_the_status_names_the_host_and_addresses()
    {
        var host = CreateHost();
        var changes = 0;
        host.StatusChanged += () => Interlocked.Increment(ref changes);

        await host.StartAsync();

        var status = host.Status;
        Assert.Equal(LanServerState.Running, status.LanServer);
        Assert.Equal(LanFixture.Label, status.MdnsName);
        Assert.Equal(["192.168.77.10"], status.Addresses);
        Assert.Null(status.PortOwner);
        Assert.Equal(LanHostProblem.None, host.Problem);
        Assert.NotNull(host.Pairing);
        Assert.True(Volatile.Read(ref changes) >= 1);
        using var http = Client();
        Assert.Equal("CLIENT-MARKER", await http.GetStringAsync("/", Ct));
        using var icon = await http.GetAsync("/apple-touch-icon.png", Ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, icon.StatusCode);
    }

    [Fact]
    public async Task Without_a_lan_address_nothing_listens_and_the_state_says_so_until_one_appears()
    {
        _addresses = [];
        var host = CreateHost();

        await host.StartAsync();

        Assert.Equal(LanServerState.NoLanAddress, host.Status.LanServer);
        Assert.Empty(host.Status.Addresses);
        Assert.True(await IsPortRefusedAsync(_httpsPort));

        ChangeNetwork(LanFixture.FakeLan);

        await LanRig.WaitForAsync(() => host.Status.LanServer, s => s == LanServerState.Running);
        using var http = Client();
        Assert.Equal("CLIENT-MARKER", await http.GetStringAsync("/", Ct));
    }

    [Fact]
    public async Task A_taken_port_gives_port_in_use_with_the_owner_and_the_server_comes_up_on_the_next_retry()
    {
        var host = CreateHost();
        var occupier = new TcpListener(IPAddress.Loopback, _httpsPort);
        occupier.Start();
        _occupiers.Add(occupier);

        await host.StartAsync();

        var status = host.Status;
        Assert.Equal(LanServerState.PortInUse, status.LanServer);
        Assert.Equal(Process.GetCurrentProcess().ProcessName, status.PortOwner);
        Assert.Equal(LanHostProblem.None, host.Problem);

        occupier.Stop();
        _fx.Time.Advance(TimeSpan.FromSeconds(30));

        await LanRig.WaitForAsync(() => host.Status.LanServer, s => s == LanServerState.Running);
        Assert.Null(host.Status.PortOwner);
        using var http = Client();
        Assert.Equal("CLIENT-MARKER", await http.GetStringAsync("/", Ct));
    }

    // Issuing replaces the fixed-name server key. If the port holder is another instance of this app (second Windows
    // session), a re-issue on every 30 s retry would keep breaking its TLS. No certificate while the port is taken.
    [Fact]
    public async Task No_certificate_is_issued_while_the_port_is_taken()
    {
        var issued = 0;
        var host = CreateHost(issue: (authority, name, addresses) =>
        {
            Interlocked.Increment(ref issued);
            return authority.IssueServerCertificate(name, addresses);
        });
        var occupier = new TcpListener(IPAddress.Loopback, _httpsPort);
        occupier.Start();
        _occupiers.Add(occupier);

        await host.StartAsync();
        _fx.Time.Advance(TimeSpan.FromSeconds(30));
        await host.ReconcileAsync();
        _fx.Time.Advance(TimeSpan.FromSeconds(30));
        await host.ReconcileAsync();

        Assert.Equal(LanServerState.PortInUse, host.Status.LanServer);
        Assert.Equal(0, Volatile.Read(ref issued));
    }

    [Fact]
    public void The_port_owner_lookup_finds_this_process_and_nobody_for_a_free_port()
    {
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        var port = ((IPEndPoint)occupier.LocalEndpoint).Port;

        Assert.Equal((uint)Environment.ProcessId, TcpPortOwner.FindListenerPid(port));
        Assert.Equal(Process.GetCurrentProcess().ProcessName, TcpPortOwner.FindListenerName(port));
        occupier.Stop();
        Assert.Null(TcpPortOwner.FindListenerName(port));
    }

    [Fact]
    public async Task A_network_change_renews_in_order_stop_server_dispose_certificate_issue_start()
    {
        // The CNG key name is reused by every certificate, so issuing while the old certificate (or the server using it)
        // is still alive would replace a key that is in use.
        var seen = new List<string>();
        X509Certificate2? previous = null;
        var host = CreateHost(issue: (authority, name, addresses) =>
        {
            if (previous is not null)
            {
                seen.Add(previous.Handle == IntPtr.Zero ? "old-certificate-disposed" : "OLD-CERTIFICATE-STILL-ALIVE");
                seen.Add(IsPortRefusedAsync(_httpsPort).GetAwaiter().GetResult() ? "old-server-stopped" : "OLD-SERVER-STILL-LISTENING");
            }

            seen.Add("issue:" + string.Join(',', addresses));
            previous = authority.IssueServerCertificate(name, addresses);
            return previous;
        });
        await host.StartAsync();
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var rigless = await ConnectAsync(token);

        var closing = LanRig.ReadCloseCodeAsync(rigless); // a phone is always reading
        ChangeNetwork(LanFixture.FakeLan, SecondLan);

        await LanRig.WaitForAsync(() => host.Status.Addresses.Count, n => n == 2);
        Assert.Equal(
            ["issue:192.168.77.10", "old-certificate-disposed", "old-server-stopped", "issue:192.168.77.10,192.168.77.22"],
            seen);
        Assert.Equal(1001, await closing); // connections of the old server are closed, not leaked

        // The new server answers with a certificate that names the new address.
        string? sans = null;
        using var handler = LanFixture.Handler(_fx.Ca.CertificateDer);
        var trust = handler.SslOptions.RemoteCertificateValidationCallback!;
        handler.SslOptions.RemoteCertificateValidationCallback = (s, c, ch, e) =>
        {
            sans = c is null ? null : new X509Certificate2(c).Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().Format(true);
            return trust(s, c, ch, e);
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri($"https://{LanFixture.Label}.local:{_httpsPort}/") };
        Assert.Equal("CLIENT-MARKER", await http.GetStringAsync("/", Ct));
        Assert.Contains("192.168.77.22", sans, StringComparison.Ordinal);
        Assert.Equal(LanServerState.Running, host.Status.LanServer);
        rigless.Dispose();
    }

    private async Task<ClientWebSocket> ConnectAsync(string token)
    {
        var invoker = new HttpMessageInvoker(LanFixture.Handler(_fx.Ca.CertificateDer), disposeHandler: true);
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", $"https://{LanFixture.Label}.local:{_httpsPort}");
        await socket.ConnectAsync(new Uri($"wss://{LanFixture.Label}.local:{_httpsPort}/ws"), invoker, Ct);
        await LanRig.SendTextAsync(socket, LanRig.Hello(token));
        Assert.Equal("""{"type":"welcome"}""", await LanRig.ReadTextAsync(socket));
        return socket;
    }

    [Fact]
    public async Task A_change_that_leaves_the_lan_addresses_equal_does_not_restart_anything()
    {
        var issued = 0;
        var host = CreateHost(issue: (authority, name, addresses) =>
        {
            Interlocked.Increment(ref issued);
            return authority.IssueServerCertificate(name, addresses);
        });
        await host.StartAsync();

        await host.ReconcileAsync();
        await host.ReconcileAsync();

        Assert.Equal(1, Volatile.Read(ref issued));
    }

    [Fact]
    public async Task Losing_every_lan_address_stops_the_server()
    {
        var host = CreateHost();
        await host.StartAsync();

        ChangeNetwork();

        await LanRig.WaitForAsync(() => host.Status.LanServer, s => s == LanServerState.NoLanAddress);
        Assert.True(await IsPortRefusedAsync(_httpsPort));
    }

    [Fact]
    public async Task An_unavailable_ca_leaves_the_server_off_and_creates_no_new_ca()
    {
        // A lone ca.cer (key missing) must never be "repaired" by generating a fresh CA: every paired phone trusts the old one.
        var broken = Path.Combine(_fx.Root, "broken-ca");
        Directory.CreateDirectory(broken);
        File.Copy(Path.Combine(_fx.Root, "ca", "ca.cer"), Path.Combine(broken, "ca.cer"));
        var host = CreateHost(caDirectory: broken);

        await host.StartAsync();

        Assert.Equal(LanHostProblem.CertificateAuthorityUnavailable, host.Problem);
        Assert.Equal(LanServerState.CaUnavailable, host.Status.LanServer);
        Assert.Null(host.Pairing);
        Assert.True(await IsPortRefusedAsync(_httpsPort));
        Assert.Equal(["ca.cer"], Directory.GetFiles(broken).Select(Path.GetFileName));
        await host.ReconcileAsync(); // still nothing
        Assert.True(await IsPortRefusedAsync(_httpsPort));
    }

    [Fact]
    public async Task Mdns_failing_to_bind_is_reported_but_https_keeps_running()
    {
        var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        _udpOccupiers.Add(udp);
        var host = CreateHost(mdnsPort: ((IPEndPoint)udp.LocalEndPoint!).Port);

        await host.StartAsync();

        Assert.Equal(LanServerState.Running, host.Status.LanServer);
        Assert.NotNull(host.MdnsProblem);
        using var http = Client();
        Assert.Equal("CLIENT-MARKER", await http.GetStringAsync("/", Ct));
    }

    [Fact]
    public async Task A_failed_multicast_join_is_reported_as_an_mdns_problem()
    {
        // 192.168.77.10 is not an address of this machine, so the join cannot succeed; the responder reports that silently.
        var host = CreateHost();

        await host.StartAsync();

        Assert.Equal(LanServerState.Running, host.Status.LanServer);
        Assert.NotNull(host.MdnsProblem);
    }
}
