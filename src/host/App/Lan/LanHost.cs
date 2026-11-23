using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ButtonBox.App.Pairing;
using ButtonBox.Core.Contracts;
using ButtonBox.Discovery;
using ButtonBox.Security;

namespace ButtonBox.App.Lan;

public sealed class LanHostOptions
{
    /// <summary>The folder holding <c>ca.cer</c> and <c>ca.key</c>.</summary>
    public required string CaDirectory { get; init; }

    /// <summary>The CNG key name that each renewal replaces.</summary>
    public required string ServerKeyName { get; init; }

    public required string ClientDir { get; init; }

    /// <summary>The logo PNG; the home-screen icon is rendered from it once.</summary>
    public required byte[] LogoPng { get; init; }

    public required string MdnsLabel { get; init; }

    /// <summary>The registry B2 loaded. When loading failed, B2 never creates a LanHost.</summary>
    public required DeviceRegistry Registry { get; init; }

    public required TemplateStore Templates { get; init; }

    public required NetworkWatcher Network { get; init; }

    public int HttpsPort { get; init; } = 443;

    public int PairingPort { get; init; } = 80;

    public int PairingFallbackPort { get; init; } = 8080;

    public int MdnsPort { get; init; } = MdnsResponder.StandardPort;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How often a failed start (port taken) is retried.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: the address a LAN address is bound to. Production binds the LAN address itself.</summary>
    public Func<LanAddress, IPAddress>? BindAddress { get; init; }

    /// <summary>Test seam: certificate issuing (default: the CA). Lets a test observe the order of renewal steps.</summary>
    public Func<CertificateAuthority, string, IReadOnlyList<IPAddress>, X509Certificate2>? IssueCertificate { get; init; }

    /// <summary>
    /// Test seam: awaited just before each step of <see cref="LanHost.RegenerateCertificateAuthorityAsync"/>. A test blocks it to hold the
    /// regeneration at a seam, or throws from it to make that step fail.
    /// </summary>
    public Func<CaRegenerationStep, Task>? RegenerationStep { get; init; }

    /// <summary>Test seam for the WebSocket limits (hello timeout, keepalive, ...).</summary>
    public DeviceConnectionsOptions? ConnectionOptions { get; init; }

    /// <summary>The game side the phones talk to. The caller owns it and disposes it after this host. Null only in tests of the LAN plumbing.</summary>
    public GameSession? Session { get; init; }

    /// <summary>The images served at <c>/assets/{sha256}</c>. Null: none.</summary>
    public IAssetSource? Assets { get; init; }
}

/// <summary>The steps of the CA regeneration that can be held or failed in a test (see <see cref="LanHost.RegenerateCertificateAuthorityAsync"/>).</summary>
public enum CaRegenerationStep
{
    /// <summary>Before the new CA is created in its sibling folder. Nothing has changed yet and the server still runs.</summary>
    CreatingNew,

    /// <summary>The server is stopped. Before <c>ca</c> is moved to <c>ca.old-…</c>.</summary>
    MovingOldAside,

    /// <summary>Half swapped: <c>ca</c> does not exist. Before <c>ca.new-…</c> is moved to <c>ca</c>.</summary>
    MovingNewIn,

    /// <summary>Fully swapped, devices not yet revoked, server still stopped.</summary>
    RevokingDevices,
}

public enum CaRegenerationResult
{
    /// <summary>The new CA is in place, every device is revoked, the server restarts from the new CA.</summary>
    Done,

    /// <summary>The LAN side has no CA or is not started (or is shutting down, or already regenerating). Nothing changed.</summary>
    Unavailable,

    /// <summary>A step failed and was rolled back: the old CA, the device list and the server are as before.</summary>
    Failed,
}

public enum LanHostProblem
{
    None,

    /// <summary>The CA files are damaged, incomplete or belong to another Windows user. Nothing was regenerated.</summary>
    CertificateAuthorityUnavailable,

    /// <summary>The server could not be started for a reason other than a taken port (see the retry).</summary>
    ServerStartFailed,
}

/// <summary>
/// Runs the LAN side: the HTTPS server, the pairing session, the phone connections and mDNS, and keeps them right when the
/// network changes (decisions §3, §9). Renewal order on every address change: stop the server, dispose the old certificate,
/// issue the new one, start the server, update mDNS.
/// </summary>
public sealed class LanHost : IAsyncDisposable
{
    private const string OldMarker = ".old-";
    private const string NewMarker = ".new-";

    private readonly LanHostOptions _o;
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private readonly object _gate = new();
    private readonly byte[] _icon;
    private readonly Func<LanAddress, IPAddress> _bind;
    private CertificateAuthority? _authority;
    private PairingSession? _pairing;
    private LanServer? _server;
    private X509Certificate2? _certificate;
    private IReadOnlyList<LanAddress> _serverAddresses = [];
    private MdnsResponder? _mdns;
    private ITimer? _retry;
    private LanServerState _state = LanServerState.Starting;
    private LanHostProblem _problem;
    private string? _portOwner;
    private string? _mdnsProblem;
    private IReadOnlyList<LanAddress> _addresses = [];
    private bool _started;
    private bool _disposed;
    private bool _regenerating;

    public LanHost(LanHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options;
        _bind = options.BindAddress ?? (a => a.Address);
        _icon = AppleTouchIcon.Render(options.LogoPng);
        Connections = new DeviceConnections(options.Registry, options.ConnectionOptions ?? new DeviceConnectionsOptions { Time = options.Time }, options.Session);
    }

    /// <summary>Raised when <see cref="Status"/>, <see cref="Problem"/> or <see cref="MdnsProblem"/> changed. Handlers must not throw or block.</summary>
    public event Action? StatusChanged;

    public DeviceConnections Connections { get; }

    /// <summary>
    /// The pairing session; null until <see cref="StartAsync"/> loaded the CA (and for good when that failed), and null while the CA is
    /// being regenerated, so that no pairing page can open on the old CA in the middle of it.
    /// </summary>
    public PairingSession? Pairing
    {
        get
        {
            lock (_gate)
            {
                return _regenerating ? null : _pairing;
            }
        }
    }

    /// <summary>SHA-256 of the CA certificate, 64 upper-case hex digits; null when there is no CA (not started, damaged, or unavailable).</summary>
    public string? CaFingerprint
    {
        get
        {
            lock (_gate)
            {
                return _authority?.Sha256Fingerprint;
            }
        }
    }

    public LanHostProblem Problem
    {
        get
        {
            lock (_gate)
            {
                return _problem;
            }
        }
    }

    /// <summary>Why mDNS does not work (UDP 5353 taken, or the multicast join failed on some interface), or null. HTTPS keeps running either way.</summary>
    public string? MdnsProblem
    {
        get
        {
            lock (_gate)
            {
                return _mdnsProblem;
            }
        }
    }

    public HostStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new HostStatus
                {
                    MdnsName = _o.MdnsLabel,
                    LanServer = _problem switch
                    {
                        LanHostProblem.CertificateAuthorityUnavailable => LanServerState.CaUnavailable,
                        LanHostProblem.ServerStartFailed => LanServerState.StartFailed,
                        _ => _state,
                    },
                    PortOwner = _state == LanServerState.PortInUse ? _portOwner : null,
                    Addresses = [.. _addresses.Select(a => a.Address.ToString())],
                };
            }
        }
    }

    /// <summary>
    /// Loads the CA (never regenerating a damaged one: the state then stays "starting" with
    /// <see cref="LanHostProblem.CertificateAuthorityUnavailable"/>) and brings the server up once.
    /// </summary>
    public async Task StartAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("Already started.");
            }

            _started = true;
        }

        try
        {
            // A crash in the middle of a regeneration is settled first, and a new CA is never created next to a ca.old-… folder.
            var pendingOld = RecoverInterruptedRegeneration(out var mustExist);
            var authority = mustExist ? LoadExisting(_o.CaDirectory) : CertificateAuthority.LoadOrCreate(_o.CaDirectory, _o.ServerKeyName);
            if (pendingOld is not null)
            {
                // The swap had completed: the phones paired with the old CA are gone (idempotent), then the old CA can go.
                try
                {
                    _o.Registry.RevokeAll();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    authority.Dispose();
                    throw new CertificateAuthorityUnavailableException("The devices could not be revoked after an interrupted regeneration.", e);
                }

                TryDeleteDirectory(pendingOld);
            }

            var pairing = CreatePairing(authority);
            lock (_gate)
            {
                _authority = authority;
                _pairing = pairing;
            }
        }
        catch (CertificateAuthorityUnavailableException)
        {
            lock (_gate)
            {
                _problem = LanHostProblem.CertificateAuthorityUnavailable;
                _addresses = _o.Network.Current;
            }

            RaiseStatusChanged();
            return;
        }

        _o.Network.Changed += OnNetworkChanged;
        _retry = _o.Time.CreateTimer(static s => ((LanHost)s!).OnRetry(), this, _o.RetryInterval, _o.RetryInterval);
        await ReconcileAsync();
    }

    /// <summary>Brings the running state in line with the current addresses. Called on network changes and by the retry timer; serialized.</summary>
    public async Task ReconcileAsync()
    {
        await _reconcile.WaitAsync();
        try
        {
            if (Volatile.Read(ref _authority) is null || _disposed)
            {
                return;
            }

            await ReconcileCoreAsync();
        }
        finally
        {
            _reconcile.Release();
        }

        RaiseStatusChanged();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _o.Network.Changed -= OnNetworkChanged;
        _retry?.Dispose();
        await _reconcile.WaitAsync();
        try
        {
            if (_pairing is not null)
            {
                await _pairing.DisposeAsync();
            }

            await StopServerAsync();
            await Connections.DisposeAsync();
            if (_mdns is not null)
            {
                await _mdns.DisposeAsync();
            }

            _authority?.Dispose();
        }
        finally
        {
            _reconcile.Release();
        }
    }

    /// <summary>
    /// Replaces the certificate authority with a new one; every phone must install the new profile and pair again. All or nothing until
    /// the commit point (the devices are revoked): a failure before it leaves the old CA files, the device list and the server as they were.
    /// Order: (1) refuse when there is no CA; cancel pairing and hold <c>_reconcile</c> for the whole operation, so no retry tick or network
    /// change can restart the server or sign in between; (2) create the new CA in <c>ca.new-…</c> and read it back while the server still
    /// runs; (3) stop the server and its certificate, move <c>ca</c> to <c>ca.old-…</c> and <c>ca.new-…</c> to <c>ca</c>; (4) revoke every
    /// device in one write; (5) commit: load the CA from <c>ca</c>, a new pairing session, delete <c>ca.old-…</c>, release the lock and
    /// reconcile, which issues the server certificate from the new CA. A crash between the steps is settled by <see cref="StartAsync"/>.
    /// </summary>
    public async Task<CaRegenerationResult> RegenerateCertificateAuthorityAsync()
    {
        lock (_gate)
        {
            if (_disposed || !_started || _authority is null || _regenerating)
            {
                return CaRegenerationResult.Unavailable;
            }

            _regenerating = true; // the pairing routes now answer "unavailable"
        }

        CaRegenerationResult result;
        try
        {
            var pairing = _pairing;
            if (pairing is not null)
            {
                try
                {
                    await pairing.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                    // Shutting down: the check below sees it.
                }
            }

            await _reconcile.WaitAsync();
            try
            {
                result = _disposed || _authority is null ? CaRegenerationResult.Unavailable : await RegenerateLockedAsync();
            }
            finally
            {
                _reconcile.Release();
            }
        }
        finally
        {
            lock (_gate)
            {
                _regenerating = false;
            }
        }

        if (result != CaRegenerationResult.Unavailable)
        {
            await ReconcileQuietlyAsync(); // success: issue from the new CA and start; failure: start again from the old CA
        }

        return result;
    }

    // Runs with _reconcile held.
    private async Task<CaRegenerationResult> RegenerateLockedAsync()
    {
        var caDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_o.CaDirectory));
        var parent = Path.GetDirectoryName(caDir)!;
        var name = Path.GetFileName(caDir);
        var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var newDir = Path.Combine(parent, name + NewMarker + suffix);
        var oldDir = Path.Combine(parent, name + OldMarker + suffix);

        // Step 2: the server keeps running. The temporary object only creates the files and is disposed at once; a second load proves the
        // key file can be decrypted and belongs to the certificate. Nothing ever signs with it.
        try
        {
            await StepAsync(CaRegenerationStep.CreatingNew);
            using (CertificateAuthority.LoadOrCreate(newDir, _o.ServerKeyName))
            {
            }

            using (LoadExisting(newDir))
            {
            }
        }
        catch (Exception)
        {
            TryDeleteDirectory(newDir);
            return CaRegenerationResult.Failed;
        }

        // Step 3 onwards: the server is down until the commit or the rollback is complete.
        await StopServerAsync();
        lock (_gate)
        {
            _state = LanServerState.Starting;
        }

        RaiseStatusChanged();

        var movedAside = false;
        var movedIn = false;
        try
        {
            await StepAsync(CaRegenerationStep.MovingOldAside);
            Directory.Move(caDir, oldDir);
            movedAside = true;
            await StepAsync(CaRegenerationStep.MovingNewIn);
            Directory.Move(newDir, caDir);
            movedIn = true;
            using (LoadExisting(caDir))
            {
            }

            // Step 4: one write. When it fails nothing changed, so the folders are simply swapped back.
            await StepAsync(CaRegenerationStep.RevokingDevices);
            _o.Registry.RevokeAll();
        }
        catch (Exception)
        {
            await RollBackAsync(caDir, oldDir, newDir, movedAside, movedIn);
            return CaRegenerationResult.Failed;
        }

        // Step 5, the commit: the devices are gone, so there is no way back. What goes wrong now is a LAN status problem.
        CertificateAuthority? loaded = null;
        try
        {
            loaded = LoadExisting(caDir);
            await InstallAuthorityAsync(loaded);
        }
        catch (Exception)
        {
            loaded?.Dispose();
            await FailClosedAsync();
            return CaRegenerationResult.Done;
        }

        TryDeleteDirectory(oldDir);
        return CaRegenerationResult.Done;
    }

    private async Task RollBackAsync(string caDir, string oldDir, string newDir, bool movedAside, bool movedIn)
    {
        try
        {
            if (movedIn)
            {
                Directory.Move(caDir, newDir);
            }

            if (movedAside)
            {
                Directory.Move(oldDir, caDir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await FailClosedAsync(); // the folders could not be put back: the startup recovery settles it, nothing is signed meanwhile
            return;
        }

        TryDeleteDirectory(newDir);

        // The old files are back. Load them afresh (the old object's key path pointed at the new key for a while) and use that object.
        var oldFingerprint = CaFingerprint;
        CertificateAuthority? reloaded = null;
        try
        {
            reloaded = LoadExisting(caDir);
            if (!string.Equals(reloaded.Sha256Fingerprint, oldFingerprint, StringComparison.Ordinal))
            {
                throw new CertificateAuthorityUnavailableException("The restored CA is not the old one.");
            }

            await InstallAuthorityAsync(reloaded);
        }
        catch (Exception)
        {
            reloaded?.Dispose();
            await FailClosedAsync();
        }
    }

    // The CA cannot be used and nothing is created in its place: the status says so, and a restart runs the recovery.
    private async Task FailClosedAsync()
    {
        PairingSession? pairing;
        CertificateAuthority? authority;
        lock (_gate)
        {
            pairing = _pairing;
            authority = _authority;
            _pairing = null;
            _authority = null;
            _problem = LanHostProblem.CertificateAuthorityUnavailable;
            _addresses = _o.Network.Current;
        }

        RaiseStatusChanged();
        if (pairing is not null)
        {
            await pairing.DisposeAsync();
        }

        authority?.Dispose();
    }

    private async Task InstallAuthorityAsync(CertificateAuthority authority)
    {
        var pairing = CreatePairing(authority);
        PairingSession? oldPairing;
        CertificateAuthority? oldAuthority;
        lock (_gate)
        {
            oldPairing = _pairing;
            oldAuthority = _authority;
            _pairing = pairing;
            _authority = authority;
            _problem = LanHostProblem.None;
        }

        if (oldPairing is not null)
        {
            await oldPairing.DisposeAsync();
        }

        oldAuthority?.Dispose();
    }

    private PairingSession CreatePairing(CertificateAuthority authority) => new(new PairingSessionOptions
    {
        Authority = authority,
        Registry = _o.Registry,
        Templates = _o.Templates,
        MdnsLabel = _o.MdnsLabel,
        BindAddresses = () => [.. _o.Network.Current.Select(_bind).Distinct()],
        HttpsPort = _o.HttpsPort,
        PrimaryPort = _o.PairingPort,
        FallbackPort = _o.PairingFallbackPort,
        Time = _o.Time,
    });

    private Task StepAsync(CaRegenerationStep step) => _o.RegenerationStep is { } hook ? hook(step) : Task.CompletedTask;

    // Loads an existing CA and never creates one: LoadOrCreate would make a new CA out of an empty folder.
    private CertificateAuthority LoadExisting(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "ca.cer")) || !File.Exists(Path.Combine(directory, "ca.key")))
        {
            throw new CertificateAuthorityUnavailableException("The CA files are missing.");
        }

        return CertificateAuthority.LoadOrCreate(directory, _o.ServerKeyName);
    }

    /// <summary>
    /// Settles what a crash in the middle of a regeneration left behind, before the CA is loaded. Returns the <c>ca.old-…</c> folder to
    /// delete once the CA loaded (the swap had completed), or null. <paramref name="mustExist"/> is true when a ca.old-… exists: the CA
    /// must then be loaded, never created. Leftover <c>ca.new-…</c> folders are ignored (nothing ever signs with them).
    /// </summary>
    private string? RecoverInterruptedRegeneration(out bool mustExist)
    {
        mustExist = false;
        var caDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_o.CaDirectory));
        var parent = Path.GetDirectoryName(caDir)!;
        if (!Directory.Exists(parent))
        {
            return null;
        }

        string[] olds;
        try
        {
            var prefix = Path.GetFileName(caDir) + OldMarker;
            olds = [.. Directory.EnumerateDirectories(parent).Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.Ordinal))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CertificateAuthorityUnavailableException("The CA folder cannot be inspected.", e);
        }

        if (olds.Length == 0)
        {
            return null;
        }

        mustExist = true;
        if (olds.Length > 1)
        {
            throw new CertificateAuthorityUnavailableException("More than one interrupted regeneration was found; nothing was touched.");
        }

        if (Directory.Exists(caDir))
        {
            return olds[0];
        }

        try
        {
            Directory.Move(olds[0], caDir); // the swap had not completed: the old CA comes back and nothing changed
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CertificateAuthorityUnavailableException("The old CA could not be restored.", e);
        }

        return null;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind: it is never used again, and the startup recovery deletes a ca.old-… folder.
        }
    }

    private async Task ReconcileCoreAsync()
    {
        var addresses = _o.Network.Current;
        var running = _server is not null && addresses.SequenceEqual(_serverAddresses);
        if (!running)
        {
            await StopServerAsync();
            if (addresses.Count == 0)
            {
                lock (_gate)
                {
                    _addresses = addresses;
                    _state = LanServerState.NoLanAddress;
                    _problem = LanHostProblem.None;
                    _portOwner = null;
                }
            }
            else
            {
                await StartServerAsync(addresses);
            }
        }

        await UpdateMdnsAsync(addresses);
    }

    // Order: the old server is already stopped and its certificate disposed (StopServerAsync), then issue, then start.
    private async Task StartServerAsync(IReadOnlyList<LanAddress> addresses)
    {
        var authority = _authority!;
        var lanIps = addresses.Select(a => a.Address).ToList();

        // Issuing replaces the fixed-name server key. While another listener holds the port (possibly another instance of
        // this app in a second Windows session, which the Local\ mutex does not see), retrying every 30 s must not keep
        // overwriting that key, so check the port before issuing.
        if (_o.HttpsPort != 0 && TcpPortOwner.FindListenerPid(_o.HttpsPort) is not null)
        {
            var holder = TcpPortOwner.FindListenerName(_o.HttpsPort);
            lock (_gate)
            {
                _addresses = addresses;
                _problem = LanHostProblem.None;
                _state = LanServerState.PortInUse;
                _portOwner = holder;
            }

            return;
        }

        X509Certificate2? certificate = null;
        try
        {
            certificate = _o.IssueCertificate is { } issue
                ? issue(authority, _o.MdnsLabel + ".local", lanIps)
                : authority.IssueServerCertificate(_o.MdnsLabel + ".local", lanIps);
            var server = await LanServer.StartAsync(new LanServerOptions
            {
                BindAddresses = [.. addresses.Select(_bind).Distinct()],
                LanAddresses = lanIps,
                MdnsLabel = _o.MdnsLabel,
                Port = _o.HttpsPort,
                Certificate = certificate,
                ClientDir = _o.ClientDir,
                AppleTouchIconPng = _icon,
                Connections = Connections,
                Pairing = _pairing!,
                Assets = _o.Assets,
                Time = _o.Time,
            });
            lock (_gate)
            {
                _server = server;
                _certificate = certificate;
                _serverAddresses = addresses;
                _addresses = addresses;
                _state = LanServerState.Running;
                _problem = LanHostProblem.None;
                _portOwner = null;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            certificate?.Dispose();
            var inUse = PortConflict.IsAddressInUse(e);
            var owner = inUse ? TcpPortOwner.FindListenerName(_o.HttpsPort) : null;
            lock (_gate)
            {
                _addresses = addresses;
                _problem = inUse ? LanHostProblem.None : LanHostProblem.ServerStartFailed;
                _state = inUse ? LanServerState.PortInUse : LanServerState.Starting;
                _portOwner = owner;
            }
        }
    }

    private async Task StopServerAsync()
    {
        LanServer? server;
        X509Certificate2? certificate;
        lock (_gate)
        {
            server = _server;
            certificate = _certificate;
            _server = null;
            _certificate = null;
            _serverAddresses = [];
        }

        if (server is null)
        {
            return;
        }

        await Connections.CloseAllAsync();
        await server.DisposeAsync();
        certificate?.Dispose(); // only now: the CNG key name is reused by the next certificate
    }

    private async Task UpdateMdnsAsync(IReadOnlyList<LanAddress> addresses)
    {
        string? problem = null;
        try
        {
            if (_mdns is null)
            {
                var responder = new MdnsResponder(_o.MdnsLabel, _o.MdnsPort);
                try
                {
                    responder.Start(addresses);
                    _mdns = responder;
                }
                catch (System.Net.Sockets.SocketException)
                {
                    await responder.DisposeAsync();
                    problem = "mDNS could not bind its UDP port; the phone cannot find the PC by name.";
                }
            }
            else
            {
                _mdns.UpdateAddresses(addresses);
            }

            if (_mdns is not null && addresses.Count > 0 && _mdns.JoinedAddresses.Count < addresses.Count)
            {
                problem = "mDNS could not join the multicast group on every LAN interface; the phone may not find the PC by name.";
            }
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidOperationException or ObjectDisposedException)
        {
            problem = "mDNS failed: " + e.GetType().Name + ".";
        }

        lock (_gate)
        {
            _mdnsProblem = problem;
        }
    }

    private void OnNetworkChanged(IReadOnlyList<LanAddress> addresses) => _ = ReconcileQuietlyAsync();

    private void OnRetry()
    {
        // Only a failed start needs retrying; a running server is handled by network changes.
        bool needed;
        lock (_gate)
        {
            needed = _server is null && !_disposed;
        }

        if (needed)
        {
            _ = ReconcileQuietlyAsync();
        }
    }

    private async Task ReconcileQuietlyAsync()
    {
        try
        {
            await ReconcileAsync();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke();
        }
        catch (Exception)
        {
            // A subscriber's failure must not break the host.
        }
    }
}
