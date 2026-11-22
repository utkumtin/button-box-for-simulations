using System.Net;
using ButtonBox.App.Lan;
using ButtonBox.App.Pairing;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Discovery;
using ButtonBox.Security;
using ButtonBox.Simulator;

namespace ButtonBox.App;

public sealed class AppHostOptions
{
    /// <summary>The per-user data folder (production: <c>%AppData%\button-box-for-simulations</c>).</summary>
    public required string DataDir { get; init; }

    /// <summary>The templates that ship with the app.</summary>
    public required string ShippedTemplatesDir { get; init; }

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
    private readonly object _gate = new();
    private LanHost? _lan;
    private Task _startTask = Task.CompletedTask;
    private bool _startFailed;
    private bool _disposed;

    private AppHost(AppHostOptions options, string? mdnsName, DeviceRegistry? registry, TemplateStore templates, SetupProblem problem, ScsSimulator simulator, DemoContent demo)
    {
        _o = options;
        _mdnsName = mdnsName;
        Registry = registry;
        Templates = templates;
        Setup = problem;
        Secret = EditorSecret.Create();
        Simulator = simulator;
        Demo = demo;
        Session = new GameSession(demo.Engine, simulator, simulator);
    }

    /// <summary>Raised when <see cref="Status"/> may have changed, on any thread. Handlers must not throw or block.</summary>
    public event Action? StatusChanged;

    public EditorSecret Secret { get; }

    /// <summary>Null when devices.json could not be loaded.</summary>
    public DeviceRegistry? Registry { get; }

    public TemplateStore Templates { get; }

    public SetupProblem Setup { get; }

    /// <summary>Until Phase 5 the Simulator (ETS2 model) is the app's only backend and state provider (decisions §3).</summary>
    public ScsSimulator Simulator { get; }

    /// <summary>The bundled read-only demo profile, catalog and images (decisions §3, §7).</summary>
    public DemoContent Demo { get; }

    /// <summary>Gestures in, outputs to the <see cref="Simulator"/>, telemetry out to every phone.</summary>
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
    /// Settings, then registry, then templates, then the bundled demo. Touches no port and no key.
    /// </summary>
    /// <exception cref="InvalidOperationException">A bundled demo file is damaged. A bug, so it fails here, loudly, and never runs with an empty profile.</exception>
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
        var demo = DemoContent.Load();
        var simulator = new ScsSimulator(options.Time);
        return new AppHost(options, settings.MdnsName, registry, templates, problem, simulator, demo);
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
                Assets = Demo.Assets,
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
