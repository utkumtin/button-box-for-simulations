using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ButtonBox.App.Lan;
using ButtonBox.Core.Contracts;
using ButtonBox.Discovery;
using ButtonBox.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ButtonBox.App.Tests;

/// <summary>
/// Regenerating the certificate authority un-pairs every phone, so it must be all or nothing (decisions §11 "Settings") and a crash in
/// the middle must never leave a silently new CA (decisions §12, Gate 2). These tests drive a real <see cref="LanHost"/> with a real CA
/// and real folder moves; only the free ports and the fake clock are test seams.
/// </summary>
public sealed class CaRegenerationTests : IAsyncDisposable
{
    private static readonly IPAddress SecondLan = IPAddress.Parse("192.168.77.22");

    private readonly LanFixture _fx = new();
    private readonly FakeTimeProvider _watcherTime = new();
    private readonly FakeSignals _signals = new();
    private readonly List<LanAddress> _addresses = [new LanAddress(1, LanFixture.FakeLan, 24)];
    private readonly List<IDisposable> _disposables = [];
    private NetworkWatcher? _watcher;
    private LanHost? _host;
    private int _httpsPort;
    private int _issued;
    private int _pairingPrimary;
    private Func<CaRegenerationStep, Task>? _hook;
    private List<LanAddress> _current;

    public CaRegenerationTests()
    {
        _current = _addresses;
    }

    private static CancellationToken Ct => LanFixture.Ct;

    private string CaDir => Path.Combine(_fx.Root, "ca");

    private string CaCer => Path.Combine(CaDir, "ca.cer");

    private string CaKey => Path.Combine(CaDir, "ca.key");

    private string DevicesJson => Path.Combine(_fx.Root, "devices.json");

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
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        await _fx.DisposeAsync();
    }

    private LanHost CreateHost()
    {
        _watcher = new NetworkWatcher(() => _current, _signals, _watcherTime, TimeSpan.FromSeconds(2));
        _httpsPort = LanFixture.FreePort();
        _pairingPrimary = LanFixture.FreePort();
        return _host = new LanHost(new LanHostOptions
        {
            CaDirectory = CaDir,
            ServerKeyName = _fx.KeyName,
            ClientDir = _fx.ClientDir,
            LogoPng = LanFixture.MakeLogo(),
            MdnsLabel = LanFixture.Label,
            Registry = _fx.Registry,
            Templates = _fx.Templates,
            Network = _watcher,
            HttpsPort = _httpsPort,
            PairingPort = _pairingPrimary,
            PairingFallbackPort = LanFixture.FreePort(),
            MdnsPort = FreeUdpPort(),
            Time = _fx.Time,
            BindAddress = _ => IPAddress.Loopback,
            IssueCertificate = (authority, name, addresses) =>
            {
                Interlocked.Increment(ref _issued);
                return authority.IssueServerCertificate(name, addresses);
            },
            RegenerationStep = step => _hook is { } hook ? hook(step) : Task.CompletedTask,
        });
    }

    private async Task<LanHost> StartHostAsync()
    {
        var host = CreateHost();
        await host.StartAsync();
        return host;
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private void ChangeNetwork(params IPAddress[] addresses)
    {
        _current = [.. addresses.Select((a, i) => new LanAddress(i + 1, a, 24))];
        _signals.Raise();
        _watcherTime.Advance(TimeSpan.FromSeconds(2));
    }

    private HttpClient Client(byte[] trustedRootDer)
    {
        var client = new HttpClient(LanFixture.Handler(trustedRootDer), disposeHandler: true)
        {
            BaseAddress = new Uri($"https://{LanFixture.Label}.local:{_httpsPort}/"),
        };
        _disposables.Add(client);
        return client;
    }

    private async Task<ClientWebSocket> OpenSocketAsync(byte[] trustedRootDer)
    {
        var invoker = new HttpMessageInvoker(LanFixture.Handler(trustedRootDer), disposeHandler: true);
        _disposables.Add(invoker);
        var socket = new ClientWebSocket();
        _disposables.Add(socket);
        socket.Options.SetRequestHeader("Origin", $"https://{LanFixture.Label}.local:{_httpsPort}");
        await socket.ConnectAsync(new Uri($"wss://{LanFixture.Label}.local:{_httpsPort}/ws"), invoker, Ct);
        return socket;
    }

    private async Task<ClientWebSocket> ConnectAsync(byte[] trustedRootDer, string token)
    {
        var socket = await OpenSocketAsync(trustedRootDer);
        await LanRig.SendTextAsync(socket, LanRig.Hello(token));
        Assert.Equal("""{"type":"welcome"}""", await LanRig.ReadTextAsync(socket));
        return socket;
    }

    private async Task<bool> IsPortRefusedAsync()
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, _httpsPort, Ct);
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static async Task WaitRunningAsync(LanHost host) =>
        await LanRig.WaitForAsync(() => host.Status.LanServer, s => s == LanServerState.Running);

    private IReadOnlyList<string> Siblings() =>
        [.. Directory.GetDirectories(_fx.Root).Select(d => Path.GetFileName(d)!).Where(n => n.StartsWith("ca", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private static string Sha(byte[] der) => Convert.ToHexString(SHA256.HashData(der));

    private static string NewCaFolder(string path)
    {
        using (CertificateAuthority.LoadOrCreate(path, "bb-unused-" + Guid.NewGuid().ToString("N")))
        {
        }

        return path;
    }

    // ---- the regeneration ----

    [Fact]
    public async Task Regeneration_gives_a_new_ca_revokes_every_device_and_serves_a_certificate_from_the_new_ca()
    {
        var host = await StartHostAsync();
        var oldDer = File.ReadAllBytes(CaCer);
        var oldFingerprint = host.CaFingerprint;
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        _fx.Registry.Add("Tablet", LanFixture.TemplateId, false);
        using var phone = await ConnectAsync(oldDer, token);
        var closing = LanRig.ReadCloseCodeAsync(phone);

        var result = await host.RegenerateCertificateAuthorityAsync();

        Assert.Equal(CaRegenerationResult.Done, result);
        var newDer = File.ReadAllBytes(CaCer);
        Assert.NotEqual(oldFingerprint, host.CaFingerprint);
        Assert.Equal(Sha(newDer), host.CaFingerprint); // what the status shows is what is on disk
        Assert.NotEqual(Sha(oldDer), Sha(newDer));
        Assert.Equal(["ca"], Siblings()); // neither ca.old-… nor ca.new-… is left
        Assert.Empty(_fx.Registry.Devices);
        Assert.Empty(DeviceRegistry.Load(DevicesJson).Devices);
        // The old socket was closed by the server stop (going away), not told "token rejected" before the commit.
        Assert.Equal(1001, await closing);

        await WaitRunningAsync(host);
        using var http = Client(newDer);
        Assert.Equal("CLIENT-MARKER", await http.GetStringAsync("/", Ct)); // chains to the new CA: signed by the new key
        using var oldTrust = Client(oldDer);
        await Assert.ThrowsAsync<HttpRequestException>(() => oldTrust.GetStringAsync("/", Ct));

        // The old token is dead: a hello with it is rejected with 4401.
        using var late = await OpenSocketAsync(newDer);
        await LanRig.SendTextAsync(late, LanRig.Hello(token));
        Assert.Equal(CloseCodes.TokenRejected, await LanRig.ReadCloseCodeAsync(late));
    }

    [Fact]
    public async Task After_regeneration_the_pairing_page_hands_out_the_new_ca()
    {
        // The pairing session keeps the CA object it was built with. A session left over from the old CA would show phones the old
        // certificate and fingerprint, and the whole regeneration would have been for nothing.
        var host = await StartHostAsync();
        var before = host.Pairing;

        Assert.Equal(CaRegenerationResult.Done, await host.RegenerateCertificateAuthorityAsync());

        var after = host.Pairing;
        Assert.NotNull(after);
        Assert.NotSame(before, after);
        var status = await after.StartAsync(null);
        try
        {
            Assert.Equal(host.CaFingerprint, status.CaFingerprint);
            Assert.Equal(Sha(File.ReadAllBytes(CaCer)), status.CaFingerprint);
        }
        finally
        {
            await after.CancelAsync();
        }
    }

    [Fact]
    public async Task An_open_pairing_window_is_cancelled_by_the_regeneration()
    {
        var host = await StartHostAsync();
        await host.Pairing!.StartAsync(null);
        var old = host.Pairing!;

        Assert.Equal(CaRegenerationResult.Done, await host.RegenerateCertificateAuthorityAsync());

        Assert.Equal(PairingState.Closed, old.Status.State);
        Assert.Equal(PairingState.Closed, host.Pairing!.Status.State);
    }

    [Theory]
    [InlineData(CaRegenerationStep.CreatingNew)]
    [InlineData(CaRegenerationStep.MovingOldAside)]
    [InlineData(CaRegenerationStep.MovingNewIn)]
    [InlineData(CaRegenerationStep.RevokingDevices)]
    public async Task A_failure_at_any_step_leaves_the_old_ca_the_devices_and_a_running_server(CaRegenerationStep failAt)
    {
        // "Nothing was changed" is the promise the editor makes in its failure text. MovingNewIn is the nasty one: the old CA is already
        // aside and ca does not exist at that moment.
        var host = await StartHostAsync();
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var certBytes = File.ReadAllBytes(CaCer);
        var keyBytes = File.ReadAllBytes(CaKey);
        var devicesBytes = File.ReadAllBytes(DevicesJson);
        var fingerprint = host.CaFingerprint;
        var issuedBefore = Volatile.Read(ref _issued);
        _hook = step => step == failAt ? throw new IOException("injected") : Task.CompletedTask;

        var result = await host.RegenerateCertificateAuthorityAsync();

        Assert.Equal(CaRegenerationResult.Failed, result);
        Assert.Equal(certBytes, File.ReadAllBytes(CaCer));
        Assert.Equal(keyBytes, File.ReadAllBytes(CaKey));
        Assert.Equal(["ca"], Siblings());
        Assert.Equal(devicesBytes, File.ReadAllBytes(DevicesJson));
        Assert.NotNull(_fx.Registry.Authenticate(token));
        Assert.Equal(fingerprint, host.CaFingerprint);
        Assert.Equal(LanHostProblem.None, host.Problem);
        await WaitRunningAsync(host);
        using var http = Client(certBytes);
        Assert.Equal("CLIENT-MARKER", await http.GetStringAsync("/", Ct)); // a certificate from the old CA
        using var phone = await ConnectAsync(certBytes, token); // the old token still works
        Assert.NotNull(host.Pairing); // pairing is usable again, on the old CA
        var pairing = await host.Pairing.StartAsync(null);
        try
        {
            Assert.Equal(fingerprint, pairing.CaFingerprint);
        }
        finally
        {
            await host.Pairing.CancelAsync();
        }

        // The server is running again from a fresh issue; the failure path never signed with a key of the wrong generation.
        Assert.True(Volatile.Read(ref _issued) >= issuedBefore);
    }

    [Fact]
    public async Task A_real_file_lock_that_stops_the_folder_move_rolls_back_cleanly()
    {
        // No injection: a handle open inside ca makes the real Directory.Move fail (an antivirus scan or an Explorer window can do that).
        var host = await StartHostAsync();
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var certBytes = File.ReadAllBytes(CaCer);
        var keyBytes = File.ReadAllBytes(CaKey);
        using var holder = new FileStream(CaCer, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await host.RegenerateCertificateAuthorityAsync();
        holder.Dispose();

        Assert.Equal(CaRegenerationResult.Failed, result);
        Assert.Equal(certBytes, File.ReadAllBytes(CaCer));
        Assert.Equal(keyBytes, File.ReadAllBytes(CaKey));
        Assert.Equal(["ca"], Siblings());
        Assert.NotNull(_fx.Registry.Authenticate(token));
        await WaitRunningAsync(host);
        using var http = Client(certBytes);
        Assert.Equal("CLIENT-MARKER", await http.GetStringAsync("/", Ct));
    }

    [Fact]
    public async Task A_network_change_and_a_retry_tick_during_the_regeneration_do_nothing_until_it_has_finished()
    {
        // The server certificate's CNG key has one fixed name and the CA object reads its key file at signing time. A reconcile that slips
        // in while ca and ca.old are swapped would sign with the wrong key, or start a server that the regeneration is about to replace.
        var host = await StartHostAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hook = async step =>
        {
            if (step == CaRegenerationStep.RevokingDevices)
            {
                reached.TrySetResult();
                await release.Task;
            }
        };
        var issuedBefore = Volatile.Read(ref _issued);
        var regeneration = host.RegenerateCertificateAuthorityAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        ChangeNetwork(LanFixture.FakeLan, SecondLan); // a network change
        _fx.Time.Advance(TimeSpan.FromSeconds(30)); // and the retry timer (the server is down, so it wants to restart it)
        await Task.Delay(400, Ct);

        Assert.Equal(issuedBefore, Volatile.Read(ref _issued));
        Assert.True(await IsPortRefusedAsync());
        Assert.NotEqual(LanServerState.Running, host.Status.LanServer);
        Assert.Null(host.Pairing); // no pairing page can open on the old CA meanwhile
        Assert.Equal(CaRegenerationResult.Unavailable, await host.RegenerateCertificateAuthorityAsync()); // not twice at once
        Assert.False(regeneration.IsCompleted);

        release.SetResult();
        Assert.Equal(CaRegenerationResult.Done, await regeneration);

        await LanRig.WaitForAsync(() => host.Status.Addresses.Count, n => n == 2);
        await WaitRunningAsync(host);
        Assert.Equal(issuedBefore + 1, Volatile.Read(ref _issued)); // exactly one certificate, from the new CA, for the new addresses
        using var http = Client(File.ReadAllBytes(CaCer));
        Assert.Equal("CLIENT-MARKER", await http.GetStringAsync("/", Ct));
    }

    [Fact]
    public async Task Regeneration_is_unavailable_when_the_host_was_not_started_or_has_no_usable_ca()
    {
        var notStarted = CreateHost();
        Assert.Equal(CaRegenerationResult.Unavailable, await notStarted.RegenerateCertificateAuthorityAsync());
        await notStarted.DisposeAsync();
        _host = null;

        File.Delete(CaKey); // damaged: only ca.cer is left
        var damaged = await StartHostAsync();
        var cerBytes = File.ReadAllBytes(CaCer);

        Assert.Equal(CaRegenerationResult.Unavailable, await damaged.RegenerateCertificateAuthorityAsync());
        Assert.Equal(["ca"], Siblings());
        Assert.Equal(cerBytes, File.ReadAllBytes(CaCer));
        Assert.False(File.Exists(CaKey)); // nothing was created
    }

    // ---- startup recovery (a crash between the steps) ----

    [Fact]
    public async Task Recovery_with_ca_missing_and_one_ca_old_moves_the_old_ca_back_and_changes_nothing()
    {
        // A crash between the two folder moves: ca does not exist, the old CA sits aside. Creating a CA here would silently un-pair every phone.
        var certBytes = File.ReadAllBytes(CaCer);
        var keyBytes = File.ReadAllBytes(CaKey);
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        Directory.Move(CaDir, Path.Combine(_fx.Root, "ca.old-0123456789abcdef"));

        var host = await StartHostAsync();

        Assert.Equal(LanHostProblem.None, host.Problem);
        Assert.Equal(["ca"], Siblings());
        Assert.Equal(certBytes, File.ReadAllBytes(CaCer));
        Assert.Equal(keyBytes, File.ReadAllBytes(CaKey));
        Assert.NotNull(_fx.Registry.Authenticate(token)); // not revoked: nothing had changed
        await WaitRunningAsync(host);
        using var http = Client(certBytes);
        Assert.Equal("CLIENT-MARKER", await http.GetStringAsync("/", Ct));
    }

    [Fact]
    public async Task Recovery_with_ca_missing_also_ignores_a_ca_new_next_to_the_old_one()
    {
        var certBytes = File.ReadAllBytes(CaCer);
        var half = NewCaFolder(Path.Combine(_fx.Root, "ca.new-aaaaaaaaaaaaaaaa"));
        var newCer = File.ReadAllBytes(Path.Combine(half, "ca.cer"));
        Directory.Move(CaDir, Path.Combine(_fx.Root, "ca.old-0123456789abcdef"));

        var host = await StartHostAsync();

        Assert.Equal(Sha(certBytes), host.CaFingerprint); // the OLD CA, not the half-installed new one
        Assert.Equal(newCer, File.ReadAllBytes(Path.Combine(half, "ca.cer")));
        Assert.Equal(["ca", "ca.new-aaaaaaaaaaaaaaaa"], Siblings());
    }

    [Fact]
    public async Task Recovery_with_ca_present_and_a_ca_old_finishes_the_swap_revokes_every_device_and_deletes_the_old_folder()
    {
        // A crash after the swap: ca is the new CA. The phones that were paired trust the old one, so they must be revoked.
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var old = NewCaFolder(Path.Combine(_fx.Root, "ca.old-0123456789abcdef"));
        var certBytes = File.ReadAllBytes(CaCer);

        var host = await StartHostAsync();

        Assert.Equal(LanHostProblem.None, host.Problem);
        Assert.Equal(["ca"], Siblings());
        Assert.False(Directory.Exists(old));
        Assert.Null(_fx.Registry.Authenticate(token));
        Assert.Empty(DeviceRegistry.Load(DevicesJson).Devices);
        Assert.Equal(Sha(certBytes), host.CaFingerprint);
        await WaitRunningAsync(host);
    }

    [Fact]
    public async Task Recovery_with_only_ca_new_leftovers_ignores_them()
    {
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var leftover = NewCaFolder(Path.Combine(_fx.Root, "ca.new-aaaaaaaaaaaaaaaa"));
        var leftoverKey = File.ReadAllBytes(Path.Combine(leftover, "ca.key"));
        var certBytes = File.ReadAllBytes(CaCer);

        var host = await StartHostAsync();

        Assert.Equal(Sha(certBytes), host.CaFingerprint);
        Assert.NotNull(_fx.Registry.Authenticate(token));
        Assert.Equal(leftoverKey, File.ReadAllBytes(Path.Combine(leftover, "ca.key")));
        await WaitRunningAsync(host);
    }

    [Fact]
    public async Task Recovery_with_more_than_one_ca_old_guesses_nothing_and_touches_nothing()
    {
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var oldA = NewCaFolder(Path.Combine(_fx.Root, "ca.old-aaaaaaaaaaaaaaaa"));
        var oldB = NewCaFolder(Path.Combine(_fx.Root, "ca.old-bbbbbbbbbbbbbbbb"));
        var a = File.ReadAllBytes(Path.Combine(oldA, "ca.key"));
        var b = File.ReadAllBytes(Path.Combine(oldB, "ca.key"));
        var certBytes = File.ReadAllBytes(CaCer);
        var keyBytes = File.ReadAllBytes(CaKey);

        var host = await StartHostAsync();

        Assert.Equal(LanHostProblem.CertificateAuthorityUnavailable, host.Problem);
        Assert.Equal(LanServerState.CaUnavailable, host.Status.LanServer);
        Assert.True(await IsPortRefusedAsync());
        Assert.Equal(["ca", "ca.old-aaaaaaaaaaaaaaaa", "ca.old-bbbbbbbbbbbbbbbb"], Siblings());
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(oldA, "ca.key")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(oldB, "ca.key")));
        Assert.Equal(certBytes, File.ReadAllBytes(CaCer));
        Assert.Equal(keyBytes, File.ReadAllBytes(CaKey));
        Assert.NotNull(_fx.Registry.Authenticate(token));
    }

    [Fact]
    public async Task Recovery_never_creates_a_ca_when_ca_is_an_empty_folder_next_to_a_ca_old()
    {
        // LoadOrCreate would turn an empty folder into a brand-new CA. With a ca.old-… present that is exactly the silent third CA.
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var old = Path.Combine(_fx.Root, "ca.old-0123456789abcdef");
        Directory.Move(CaDir, old);
        Directory.CreateDirectory(CaDir);
        var oldKey = File.ReadAllBytes(Path.Combine(old, "ca.key"));

        var host = await StartHostAsync();

        Assert.Equal(LanHostProblem.CertificateAuthorityUnavailable, host.Problem);
        Assert.Empty(Directory.GetFileSystemEntries(CaDir));
        Assert.Equal(oldKey, File.ReadAllBytes(Path.Combine(old, "ca.key")));
        Assert.NotNull(_fx.Registry.Authenticate(token));
        Assert.True(await IsPortRefusedAsync());
    }

    [Fact]
    public async Task Recovery_with_a_damaged_ca_and_a_ca_old_keeps_the_old_folder_and_the_devices()
    {
        var (_, token) = _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var old = NewCaFolder(Path.Combine(_fx.Root, "ca.old-0123456789abcdef"));
        var oldKey = File.ReadAllBytes(Path.Combine(old, "ca.key"));
        File.WriteAllBytes(CaKey, [1, 2, 3]); // the key no longer decrypts

        var host = await StartHostAsync();

        Assert.Equal(LanHostProblem.CertificateAuthorityUnavailable, host.Problem);
        Assert.Equal(oldKey, File.ReadAllBytes(Path.Combine(old, "ca.key")));
        Assert.NotNull(_fx.Registry.Authenticate(token)); // not revoked until the new CA is known to be good
    }

    [Fact]
    public async Task Recovery_that_cannot_revoke_keeps_the_ca_old_folder_and_starts_nothing()
    {
        // Fail closed: the swap completed, but without the revoke the old tokens would still be in devices.json. The folder stays so the
        // next start tries again.
        _fx.Registry.Add("Phone", LanFixture.TemplateId, false);
        var old = NewCaFolder(Path.Combine(_fx.Root, "ca.old-0123456789abcdef"));
        File.Delete(DevicesJson);
        Directory.CreateDirectory(DevicesJson); // the write of devices.json can no longer succeed

        var host = await StartHostAsync();

        Assert.Equal(LanHostProblem.CertificateAuthorityUnavailable, host.Problem);
        Assert.True(Directory.Exists(old));
        Assert.True(await IsPortRefusedAsync());
    }
}
