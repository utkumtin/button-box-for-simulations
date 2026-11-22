using System.Net;
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

    /// <summary>Test seam for the WebSocket limits (hello timeout, keepalive, ...).</summary>
    public DeviceConnectionsOptions? ConnectionOptions { get; init; }
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

    public LanHost(LanHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options;
        _bind = options.BindAddress ?? (a => a.Address);
        _icon = AppleTouchIcon.Render(options.LogoPng);
        Connections = new DeviceConnections(options.Registry, options.ConnectionOptions ?? new DeviceConnectionsOptions { Time = options.Time });
    }

    /// <summary>Raised when <see cref="Status"/>, <see cref="Problem"/> or <see cref="MdnsProblem"/> changed. Handlers must not throw or block.</summary>
    public event Action? StatusChanged;

    public DeviceConnections Connections { get; }

    /// <summary>The pairing session; null until <see cref="StartAsync"/> loaded the CA (and for good when that failed).</summary>
    public PairingSession? Pairing
    {
        get
        {
            lock (_gate)
            {
                return _pairing;
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
            var authority = CertificateAuthority.LoadOrCreate(_o.CaDirectory, _o.ServerKeyName);
            var pairing = new PairingSession(new PairingSessionOptions
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
