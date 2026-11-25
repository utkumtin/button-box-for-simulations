using System.Net;
using ButtonBox.App.Lan;
using ButtonBox.App.Pairing;
using ButtonBox.App.Profiles;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Discovery;
using ButtonBox.Output.Scs;
using ButtonBox.Output.Scs.Install;
using ButtonBox.Security;
using ButtonBox.Simulator;

namespace ButtonBox.App;

public sealed class AppHostOptions
{
    /// <summary>The per-user data folder (production: <c>%AppData%\button-box-for-simulations</c>).</summary>
    public required string DataDir { get; init; }

    /// <summary>The templates that ship with the app.</summary>
    public required string ShippedTemplatesDir { get; init; }

    /// <summary>The action catalogs that ship with the app (<c>catalog/*.json</c>).</summary>
    public required string ShippedCatalogsDir { get; init; }

    public required string ClientDir { get; init; }

    public required byte[] LogoPng { get; init; }

    /// <summary>The caller owns and disposes it.</summary>
    public required NetworkWatcher Network { get; init; }

    public string ServerKeyName { get; init; } = ProductInfo.Name + " server";

    public int HttpsPort { get; init; } = 443;

    public int PairingPort { get; init; } = 80;

    public int PairingFallbackPort { get; init; } = 8080;

    public int MdnsPort { get; init; } = MdnsResponder.StandardPort;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Test seam, see <see cref="LanHostOptions.BindAddress"/>.</summary>
    public Func<LanAddress, IPAddress>? BindAddress { get; init; }

    /// <summary>The <c>--simulator</c> flag: every profile runs on the Simulator (decisions §3 "Backend selection").</summary>
    public bool UseSimulator { get; init; }

    // The SCS pieces are given by Program.Main only. Without the locator and the bridge options the app runs on the Simulator,
    // whatever UseSimulator says, so a test can never reach the real pipe or the real Steam setup by accident.

    /// <summary>Finds the installed games. Production: <c>new SteamGameLocator()</c>.</summary>
    public IScsGameLocator? ScsLocator { get; init; }

    /// <summary>Pipe name and timings of the bridge. Production: the defaults (the real pipe name).</summary>
    public ScsBridgeOptions? ScsBridgeOptions { get; init; }

    /// <summary>Full path of the plugin DLL that ships with the app; null or a missing file means "not included in this build".</summary>
    public string? ScsPluginDll { get; init; }
}

/// <summary>Why the LAN side cannot start. Nothing is ever replaced by an empty or new file.</summary>
public enum SetupProblem
{
    None,

    /// <summary>settings.json exists but is not valid, or could not be written. The mDNS name is unknown.</summary>
    SettingsUnavailable,

    /// <summary>devices.json exists but cannot be read. An empty registry would silently un-pair every phone.</summary>
    DevicesUnavailable,
}

/// <summary>
/// The app's data and LAN side, without any UI: settings, device registry, templates, the editor secret and the
/// <see cref="LanHost"/>. Load failures are kept as a state, never turned into empty data (decisions §8, §12).
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly AppHostOptions _o;
    private readonly string? _mdnsName;
    private readonly ScsBridge? _bridge;
    private readonly object _gate = new();
    private LanHost? _lan;
    private Task _startTask = Task.CompletedTask;
    private bool _startFailed;
    private bool _disposed;

    private AppHost(
        AppHostOptions options, string? mdnsName, DeviceRegistry? registry, TemplateStore templates, SetupProblem problem, ScsSimulator? simulator,
        ScsBridge? bridge, IStateProvider state, IScsPluginInstaller? installer, CatalogStore catalogs, AssetLibrary assets, ProfileStore profiles,
        GameSession session, IReadOnlyList<string> firstRunProblems)
    {
        _o = options;
        _mdnsName = mdnsName;
        Registry = registry;
        Templates = templates;
        Setup = problem;
        Secret = EditorSecret.Create();
        Simulator = simulator;
        _bridge = bridge;
        State = state;
        ScsInstaller = installer;
        Catalogs = catalogs;
        Assets = assets;
        Profiles = profiles;
        Session = session;
        FirstRunProblems = firstRunProblems;
    }

    /// <summary>Raised when <see cref="Status"/> may have changed, on any thread. Handlers must not throw or block.</summary>
    public event Action? StatusChanged;

    public EditorSecret Secret { get; }

    /// <summary>Null when devices.json could not be loaded.</summary>
    public DeviceRegistry? Registry { get; }

    public TemplateStore Templates { get; }

    public SetupProblem Setup { get; }

    /// <summary>The Simulator, only when it is the backend (<c>--simulator</c>, or no SCS pieces were given); null otherwise (decisions §3).</summary>
    public ScsSimulator? Simulator { get; }

    /// <summary>The state provider the session reads: the Simulator, or the router over the SCS bridge. The editor's telemetry screen reads it too.</summary>
    public IStateProvider State { get; }

    /// <summary>The SCS link state (Settings panel); null while there is no bridge (simulator mode).</summary>
    public ScsBridgeStatus? ScsLink => _bridge?.Status;

    /// <summary>Installs the plugin into the games; null when no locator was given. Does file I/O: never call it on the UI thread.</summary>
    public IScsPluginInstaller? ScsInstaller { get; }

    /// <summary>The action catalogs that ship with the app (decisions §4).</summary>
    public CatalogStore Catalogs { get; }

    /// <summary>The user's images (decisions §7); the LAN <c>/assets</c> route serves from it.</summary>
    public AssetLibrary Assets { get; }

    /// <summary>The user's profiles and the active one (decisions §6). Save and activation publish to <see cref="Session"/>.</summary>
    public ProfileStore Profiles { get; }

    /// <summary>Why the first-run seed did not complete; empty when it was not needed or succeeded.</summary>
    public IReadOnlyList<string> FirstRunProblems { get; }

    /// <summary>Gestures in, outputs to the backend (the SCS bridge or the Simulator), telemetry out to every phone.</summary>
    public GameSession Session { get; }

    /// <summary>The LAN side once <see cref="StartAsync"/> created it.</summary>
    public LanHost? Lan
    {
        get
        {
            lock (_gate)
            {
                return _lan;
            }
        }
    }

    public HostStatus Status
    {
        get
        {
            var lan = Lan;
            bool startFailed;
            lock (_gate)
            {
                startFailed = _startFailed;
            }

            var addresses = (IReadOnlyList<string>)[.. _o.Network.Current.Select(a => a.Address.ToString())];
            return Setup switch
            {
                SetupProblem.SettingsUnavailable => new HostStatus { MdnsName = string.Empty, LanServer = LanServerState.StartFailed, Addresses = addresses },
                SetupProblem.DevicesUnavailable => new HostStatus { MdnsName = _mdnsName!, LanServer = LanServerState.DevicesUnavailable, Addresses = addresses },
                _ when startFailed => new HostStatus { MdnsName = _mdnsName!, LanServer = LanServerState.StartFailed, Addresses = addresses },
                _ when lan is null => new HostStatus { MdnsName = _mdnsName!, LanServer = LanServerState.Starting, Addresses = addresses },
                _ => lan.Status,
            };
        }
    }

    /// <summary>
    /// Settings, then registry, templates, catalogs, the asset library and the profiles (seeded on the first run). Touches no port and no key.
    /// </summary>
    /// <exception cref="InvalidOperationException">A bundled demo file is damaged. A bug, so it fails here, loudly.</exception>
    public static AppHost Create(AppHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = AppSettings.LoadOrCreate(options.DataDir);
        var problem = settings.Problem is null ? SetupProblem.None : SetupProblem.SettingsUnavailable;

        // Loaded even when settings failed: the devices page keeps working.
        DeviceRegistry? registry = null;
        try
        {
            registry = DeviceRegistry.Load(Path.Combine(options.DataDir, "devices.json"), options.Time);
        }
        catch (DeviceRegistryException)
        {
            problem = problem == SetupProblem.None ? SetupProblem.DevicesUnavailable : problem;
        }

        var templates = new TemplateStore(options.ShippedTemplatesDir, Path.Combine(options.DataDir, "templates"));
        var catalogs = new CatalogStore(options.ShippedCatalogsDir);
        var assets = new AssetLibrary(Path.Combine(options.DataDir, "assets"));
        var installer = options.ScsLocator is { } found ? new ScsPluginInstaller(new PluginInstaller(found, options.ScsPluginDll)) : null;

        // One clock for the engines, the session and the Simulator: a layout engine's next deadline is an absolute time.
        ScsSimulator? simulator = null;
        ScsBridge? bridge = null;
        IOutputBackend backend;
        IStateProvider state;
        GameSession? session = null;
        if (options.UseSimulator || options.ScsLocator is null || options.ScsBridgeOptions is null)
        {
            simulator = new ScsSimulator(options.Time);
            backend = simulator;
            state = simulator;
        }
        else
        {
            bridge = new ScsBridge(options.ScsLocator, options.ScsBridgeOptions, options.Time);
            var router = new GameBackend(() => session?.Engine?.Game, bridge, bridge);
            backend = router;
            state = router;
        }

        // The store publishes to the session and the session starts without a profile; the active engine is published once the
        // store (and the first-run seed) are ready. No phone is connected yet.
        session = new GameSession(null, backend, state, options.Time);
        var profiles = new ProfileStore(options.DataDir, new ProfileGate(catalogs, templates, assets), catalogs, session, options.Time);
        var firstRun = FirstRun.Run(options.DataDir, assets, profiles, DemoContent.ReadSeed());
        session.Publish(profiles.CreateActiveEngine(), keepSwitchPositions: false);
        bridge?.Start(); // binds no port; connects to the game's pipe whenever one serves it
        return new AppHost(options, settings.MdnsName, registry, templates, problem, simulator, bridge, state, installer, catalogs, assets, profiles, session, firstRun);
    }

    /// <summary>
    /// Brings the LAN side up. Never throws: a failure becomes the status <c>startFailed</c>. Does nothing when
    /// <see cref="Setup"/> is a problem.
    /// </summary>
    public Task StartAsync()
    {
        if (Setup != SetupProblem.None)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            if (_disposed || _lan is not null || _startFailed)
            {
                return _startTask;
            }

            _startTask = RunStartAsync();
            return _startTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task start;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            start = _startTask;
        }

        // A quit during start must not leave a half-started LAN side behind.
        await start.ConfigureAwait(false);
        var lan = Lan;
        if (lan is not null)
        {
            await lan.DisposeAsync();
        }

        await Session.DisposeAsync(); // after the phones are gone
        if (_bridge is not null)
        {
            await _bridge.DisposeAsync(); // after the session: nothing presses through it any more
        }
    }

    private async Task RunStartAsync()
    {
        await Task.Yield(); // never run the start on the caller's stack (the caller holds the lock)
        try
        {
            var lan = new LanHost(new LanHostOptions
            {
                CaDirectory = Path.Combine(_o.DataDir, "ca"),
                ServerKeyName = _o.ServerKeyName,
                ClientDir = _o.ClientDir,
                LogoPng = _o.LogoPng,
                MdnsLabel = _mdnsName!,
                Registry = Registry!,
                Templates = Templates,
                Network = _o.Network,
                HttpsPort = _o.HttpsPort,
                PairingPort = _o.PairingPort,
                PairingFallbackPort = _o.PairingFallbackPort,
                MdnsPort = _o.MdnsPort,
                Time = _o.Time,
                BindAddress = _o.BindAddress,
                Session = Session,
                Assets = Assets,
            });
            lan.StatusChanged += RaiseStatusChanged;
            lock (_gate)
            {
                _lan = lan;
            }

            RaiseStatusChanged();
            await lan.StartAsync();
        }
        catch (Exception)
        {
            // The message of the exception is not shown: it may name paths or key material.
            lock (_gate)
            {
                _startFailed = true;
            }
        }

        RaiseStatusChanged();
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

/// <summary>The tray balloon text for a status that needs the user's attention; null when none does. Fixed text, no secrets.</summary>
public static class StatusMessages
{
    public static string? For(HostStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.LanServer switch
        {
            LanServerState.PortInUse => "TCP port 443 is in use by " + (string.IsNullOrWhiteSpace(status.PortOwner) ? "another program" : status.PortOwner)
                + ". Phones cannot connect until it is closed; the app retries automatically.",
            LanServerState.CaUnavailable => "The certificate authority files are damaged or belong to another Windows user. Phones cannot connect. Nothing was regenerated.",
            LanServerState.DevicesUnavailable => "devices.json cannot be read, so the LAN server was not started. The file was not changed.",
            LanServerState.StartFailed => "The LAN server could not start (settings or server error). Phones cannot connect.",
            _ => null,
        };
    }
}
