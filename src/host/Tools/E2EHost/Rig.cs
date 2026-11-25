using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ButtonBox.App;
using ButtonBox.App.Lan;
using ButtonBox.App.Pairing;
using ButtonBox.App.Profiles;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;
using ButtonBox.Simulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ButtonBox.E2EHost;

/// <summary>
/// The real LAN server, WebSocket layer, game session, profile store (seeded with the first-run profile), editor server and
/// Simulator on loopback, with a throwaway CA, data folder and key name, plus a loopback-only control endpoint for the tests.
/// Nothing here is written to a log.
/// </summary>
internal sealed class Rig : IDisposable
{
    private const string SecretHeader = "X-E2E-Secret";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly string _keyName = "bb-e2e-" + Guid.NewGuid().ToString("N");
    private readonly string _secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private readonly byte[] _secretBytes;
    // One clock for the Simulator, the session and the store's engines (a layout engine's deadline is an absolute time).
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly ScsSimulator _simulator;
    private readonly object _cleanup = new();
    private CertificateAuthority? _ca;
    private X509Certificate2? _certificate;
    private DeviceConnections? _connections;
    private GameSession? _session;
    private LocalServer? _editor;
    private EditorSecret? _editorSecret;
    private PairingSession? _pairing;
    private LanServer? _lan;
    private WebApplication? _control;
    private int _controlPort;
    private (DeviceRecord Device, string Token) _phone;
    private (DeviceRecord Device, string Token) _noLayout;
    private HashSet<string>? _knownInputs;
    private bool _cleaned;

    public Rig()
    {
        _secretBytes = Encoding.UTF8.GetBytes(_secret);
        _simulator = new ScsSimulator(_time);
    }

    public async Task StartAsync(string clientDir, string editorDir)
    {
        Directory.CreateDirectory(_root);
        var shipped = Path.Combine(AppContext.BaseDirectory, "templates");
        var templates = new TemplateStore(shipped, Path.Combine(_root, "templates"));
        if (!templates.TryGet("iphone-14-pro", out _))
        {
            throw new InvalidOperationException("The shipped iphone-14-pro template was not found next to the E2E host.");
        }

        _ca = CertificateAuthority.LoadOrCreate(Path.Combine(_root, "ca"), _keyName);
        var registry = DeviceRegistry.Load(Path.Combine(_root, "devices.json"));
        _phone = registry.Add("E2E phone", "iphone-14-pro", screenMismatch: false);
        var screen = new ScreenInfo
        {
            Width = 700,
            Height = 300,
            PixelRatio = 2,
            SafeArea = new SafeArea { Top = 0, Right = 0, Bottom = 0, Left = 0 },
        };
        _noLayout = registry.Add("E2E phone without layout", templates.GetOrAddGeneric(screen).Id, screenMismatch: false);

        // The same composition as AppHost.Create, on the throwaway data folder: the store publishes to the session, the first run
        // seeds "ETS2 starter" and the session then starts from the active engine.
        var catalogs = new CatalogStore(Path.Combine(AppContext.BaseDirectory, "catalog"));
        var assets = new AssetLibrary(Path.Combine(_root, "assets"));
        _knownInputs = catalogs.TryGet(Game.Ets2, out var ets2)
            ? ets2.Actions.Select(a => a.Output).OfType<ScsOutput>().Select(o => o.Input).ToHashSet(StringComparer.Ordinal)
            : throw new InvalidOperationException("The shipped ETS2 catalog was not found next to the E2E host.");
        _session = new GameSession(null, _simulator, _simulator, _time);
        var profiles = new ProfileStore(_root, new ProfileGate(catalogs, templates, assets), catalogs, _session, _time);
        var firstRun = FirstRun.Run(_root, assets, profiles, DemoContent.ReadSeed());
        if (firstRun.Count > 0)
        {
            throw new InvalidOperationException("The first-run seed did not complete.");
        }

        _session.Publish(profiles.CreateActiveEngine(), keepSwitchPositions: false);
        _connections = new DeviceConnections(registry, new DeviceConnectionsOptions(), _session);

        const string label = "buttonbox-e2e";
        var loopback = IPAddress.Loopback;
        _certificate = _ca.IssueServerCertificate(label + ".local", []); // loopback is not a permitted SAN; the browser ignores the name mismatch (ignoreHTTPSErrors)
        _pairing = new PairingSession(new PairingSessionOptions
        {
            Authority = _ca,
            Registry = registry,
            Templates = templates,
            MdnsLabel = label,
            BindAddresses = () => [loopback],
            HttpsPort = 1, // never opened: nothing here starts a pairing
        });
        using var logo = typeof(AppHost).Assembly.GetManifestResourceStream("tray-logo.png")
            ?? throw new InvalidOperationException("The bundled logo is missing.");
        using var logoBytes = new MemoryStream();
        await logo.CopyToAsync(logoBytes);
        _lan = await LanServer.StartAsync(new LanServerOptions
        {
            BindAddresses = [loopback],
            LanAddresses = [loopback], // the page is loaded from https://127.0.0.1:<port>, so this is its Origin
            MdnsLabel = label,
            Port = 0,
            Certificate = _certificate,
            ClientDir = clientDir,
            AppleTouchIconPng = AppleTouchIcon.Render(logoBytes.ToArray()),
            Connections = _connections,
            Pairing = _pairing,
            Assets = assets,
        });

        // The editor's loopback server with its API, as Program.cs wires it. No CA regeneration (503), a fixed firewall answer
        // (the real probe would read this machine's rules), nothing else is left out.
        _editorSecret = EditorSecret.Create();
        _editor = await LocalServer.StartAsync(clientDir, editorDir, new EditorApiOptions
        {
            Secret = _editorSecret,
            Registry = registry,
            Templates = templates,
            Pairing = () => _pairing,
            IsConnected = id => _connections.IsConnected(id),
            Status = () => new HostStatus { MdnsName = label, LanServer = LanServerState.Running, Addresses = [] },
            Profiles = profiles,
            Catalogs = catalogs,
            Assets = assets,
            Session = _session,
            Telemetry = _simulator,
            CaFingerprint = () => _ca.Sha256Fingerprint,
            Firewall = new FirewallStatus(() => FirewallState.Present, TimeProvider.System),
            // No ScsInstaller: the settings say "plugin not included" and both games "game not found"; no Steam lookup happens here.
        });

        await StartControlAsync();
    }

    /// <summary>The one line for the test runner: ports, control secret and both device records. Printed to stdout only.</summary>
    public string ReadyLine() => JsonSerializer.Serialize(
        new
        {
            port = _lan!.Port,
            controlPort = _controlPort,
            editorPort = _editor!.BaseAddress.Port,
            editorSecret = _editorSecret!.Value, // the value of the bbs_editor cookie; stdout only
            secret = _secret,
            devices = new
            {
                phone = new { deviceId = _phone.Device.Id, token = _phone.Token },
                noLayout = new { deviceId = _noLayout.Device.Id, token = _noLayout.Token },
            },
        },
        Json);

    public async Task StopAsync()
    {
        await Quietly(async () =>
        {
            if (_control is not null)
            {
                await _control.DisposeAsync();
            }
        });
        await Quietly(async () =>
        {
            if (_editor is not null)
            {
                await _editor.DisposeAsync();
            }
        });
        await Quietly(async () =>
        {
            if (_lan is not null)
            {
                await _lan.DisposeAsync();
            }
        });
        await Quietly(async () =>
        {
            if (_pairing is not null)
            {
                await _pairing.DisposeAsync();
            }
        });
        await Quietly(async () =>
        {
            if (_connections is not null)
            {
                await _connections.DisposeAsync();
            }
        });
        await Quietly(async () =>
        {
            if (_session is not null)
            {
                await _session.DisposeAsync();
            }
        });
        Cleanup();
    }

    /// <summary>Deletes the CNG key and the data folder. Idempotent; also runs from ProcessExit.</summary>
    public void Cleanup()
    {
        lock (_cleanup)
        {
            if (_cleaned)
            {
                return;
            }

            _cleaned = true;
        }

        try
        {
            _certificate?.Dispose();
            _ca?.Dispose();
        }
        catch (Exception)
        {
            // Best effort.
        }

        try
        {
            CertificateAuthority.DeleteServerKey(_keyName);
        }
        catch (Exception)
        {
            // Best effort: the key name is unique to this run.
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp folder.
        }
        catch (UnauthorizedAccessException)
        {
            // A temp folder.
        }
    }

    public void Dispose() => Cleanup();

    private static async Task Quietly(Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception)
        {
            // Keep stopping the rest.
        }
    }

    private async Task StartControlAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Run(HandleControlAsync);
        await app.StartAsync();
        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _controlPort = new Uri(bound).Port;
        _control = app;
    }

    // GET /state, POST /state {telemetry?, gameListening?, engineNeverStarts?}, GET /received, POST /game {input}, POST /drop. All need the secret header.
    private async Task HandleControlAsync(HttpContext context)
    {
        var response = context.Response;
        var sent = Encoding.UTF8.GetBytes(context.Request.Headers[SecretHeader].ToString());
        if (!CryptographicOperations.FixedTimeEquals(sent, _secretBytes))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var path = context.Request.Path.Value;
        var post = HttpMethods.IsPost(context.Request.Method);
        var get = HttpMethods.IsGet(context.Request.Method);
        if (post && path == "/state")
        {
            var change = await JsonSerializer.DeserializeAsync<StateChange>(context.Request.Body, Json, context.RequestAborted);
            if (change is null)
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (change.Telemetry is { } telemetry)
            {
                _simulator.TelemetryAvailable = telemetry;
            }

            if (change.GameListening is { } listening)
            {
                _simulator.GameListening = listening;
            }

            if (change.EngineNeverStarts is { } never)
            {
                _simulator.EngineNeverStarts = never;
            }

            await WriteStateAsync(context);
        }
        else if (get && path == "/state")
        {
            await WriteStateAsync(context);
        }
        else if (get && path == "/received")
        {
            var received = _simulator.Received.Select(r => new
            {
                kind = r.Kind.ToString().ToLowerInvariant(),
                name = r.Output switch
                {
                    ScsInput scs => scs.Name,
                    VJoyButton vjoy => "vjoy:" + vjoy.Number,
                    _ => "unknown",
                },
            });
            await WriteJsonAsync(context, received);
        }
        else if (post && path == "/game")
        {
            await HandleGameInputAsync(context);
        }
        else if (post && path == "/drop")
        {
            await _connections!.CloseAllAsync();
            response.StatusCode = StatusCodes.Status204NoContent;
        }
        else
        {
            response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    // POST /game {"input": "<scs input>"}: the user pressing the game's own keyboard binding. It goes straight to the Simulator, past the
    // session and the output queue, so the engines only learn of it through telemetry. The name must be one of the ETS2 catalog's inputs.
    private async Task HandleGameInputAsync(HttpContext context)
    {
        string? name = null;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var root = body.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var properties = root.EnumerateObject().ToList();
                if (properties.Count == 1 && properties[0].Name == "input" && properties[0].Value.ValueKind == JsonValueKind.String)
                {
                    name = properties[0].Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // name stays null: 400.
        }

        if (name is null || _knownInputs is null || !_knownInputs.Contains(name))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await _simulator.PressAsync(new ScsInput(name), context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private Task WriteStateAsync(HttpContext context) => WriteJsonAsync(
        context,
        new
        {
            telemetry = _simulator.TelemetryAvailable,
            gameListening = _simulator.GameListening,
            engineNeverStarts = _simulator.EngineNeverStarts,
            connected = _connections!.ConnectedDeviceIds.Count,
        });

    private static async Task WriteJsonAsync(HttpContext context, object value)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(context.Response.Body, value, value.GetType(), Json, context.RequestAborted);
    }

    private sealed record StateChange(bool? Telemetry, bool? GameListening, bool? EngineNeverStarts);
}
