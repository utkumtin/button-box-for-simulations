using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ButtonBox.App.Pairing;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace ButtonBox.App.Lan;

public sealed class LanServerOptions
{
    /// <summary>The addresses to listen on, one listener each. Production: the LAN addresses themselves; tests: loopback.</summary>
    public required IReadOnlyList<IPAddress> BindAddresses { get; init; }

    /// <summary>The LAN addresses a phone may use in its URL. Together with the mDNS name they form the Origin allow-list.</summary>
    public required IReadOnlyList<IPAddress> LanAddresses { get; init; }

    public required string MdnsLabel { get; init; }

    /// <summary>443 in production. 0 (any free port) is for tests.</summary>
    public required int Port { get; init; }

    /// <summary>The server certificate with its private key. The caller owns it and disposes it after this server stopped.</summary>
    public required X509Certificate2 Certificate { get; init; }

    public required string ClientDir { get; init; }

    /// <summary>180x180 PNG, see <see cref="AppleTouchIcon.Render"/>.</summary>
    public required byte[] AppleTouchIconPng { get; init; }

    public required DeviceConnections Connections { get; init; }

    public required PairingSession Pairing { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The LAN HTTPS server (decisions §3): the phone client, <c>/api/pair</c>, <c>/ws</c> and the home-screen icon. Nothing else.
/// The editor API never exists here.
/// </summary>
public sealed class LanServer : IAsyncDisposable
{
    private const int MaxPairBodyBytes = 2048;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly WebApplication _app;
    private readonly LanServerOptions _o;
    private readonly TokenBucket _pairBucket;
    private readonly byte[] _icon;
    private HashSet<string> _allowedOrigins = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    private LanServer(WebApplication app, LanServerOptions options)
    {
        _app = app;
        _o = options;
        _icon = options.AppleTouchIconPng;
        // Global, not per IP: the pairing code has 6 digits, so the total number of guesses is what matters.
        _pairBucket = new TokenBucket(10, 1, options.Time);
    }

    /// <summary>The port actually bound (the first listener's).</summary>
    public int Port { get; private set; }

    /// <exception cref="IOException">Binding failed; see <see cref="PortConflict.IsAddressInUse"/>.</exception>
    public static async Task<LanServer> StartAsync(LanServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BindAddresses.Count == 0)
        {
            throw new ArgumentException("At least one address is required.", nameof(options));
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = StopTimeout);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxConcurrentConnections = 64;
            // Upgraded (HTTP/1.1 WebSocket) connections leave the counter above: at most 64 devices plus the unauthenticated ones.
            o.Limits.MaxConcurrentUpgradedConnections = 72;
            o.Limits.MaxRequestBodySize = 4 * 1024;
            o.Limits.MaxRequestHeadersTotalSize = 8 * 1024;
            o.Limits.MaxRequestHeaderCount = 40;
            o.Limits.MaxRequestLineSize = 4 * 1024;
            o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            foreach (var address in options.BindAddresses)
            {
                o.Listen(address, options.Port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = options.Certificate,
                        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    });
                });
            }
        });

        var app = builder.Build();
        var server = new LanServer(app, options);
        server.Configure();
        if (options.Port != 0)
        {
            server.Port = options.Port;
            server.BuildOrigins(); // before the first request can arrive
        }

        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.Port = new Uri(bound).Port;
        server.BuildOrigins();
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var cts = new CancellationTokenSource(StopTimeout);
        try
        {
            await _app.StopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Connections that did not finish in time are cut when the app is disposed.
        }

        await _app.DisposeAsync();
    }

    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && parsed.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        && (parsed.Charset.Length == 0 || parsed.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    private void Configure()
    {
        var files = Directory.Exists(_o.ClientDir)
            ? (IFileProvider)new PhysicalFileProvider(Path.GetFullPath(_o.ClientDir))
            : new NullFileProvider();
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";

        _app.UseWebSockets();
        _app.UseSecurityHeaders();
        _app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.Equals("/api/pair", StringComparison.Ordinal) && HttpMethods.IsPost(context.Request.Method))
            {
                await HandlePairAsync(context);
            }
            else if (path.Equals("/ws", StringComparison.Ordinal))
            {
                // No method check: HTTP/2 WebSockets use CONNECT. IsWebSocketRequest decides.
                if (OriginAllowed(context.Request))
                {
                    await _o.Connections.HandleAsync(context);
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                }
            }
            else if (path.Equals("/apple-touch-icon.png", StringComparison.Ordinal) && HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.ContentType = "image/png";
                context.Response.Headers.CacheControl = "max-age=86400";
                context.Response.ContentLength = _icon.Length;
                await context.Response.Body.WriteAsync(_icon, context.RequestAborted);
            }
            else
            {
                await next();
            }
        });
        _app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        _app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = contentTypes });
    }

    private void BuildOrigins()
    {
        var suffix = Port == 443 ? string.Empty : ":" + Port.ToString(CultureInfo.InvariantCulture);
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"https://{_o.MdnsLabel}.local{suffix}" };
        foreach (var address in _o.LanAddresses)
        {
            origins.Add($"https://{address}{suffix}");
        }

        _allowedOrigins = origins;
    }

    // A missing Origin is refused: browsers always send it on a WebSocket handshake and on a POST, so only a non-browser omits it.
    private bool OriginAllowed(HttpRequest request)
    {
        var values = request.Headers.Origin;
        return values.Count == 1 && values[0] is { } origin && _allowedOrigins.Contains(origin);
    }

    private async Task HandlePairAsync(HttpContext context)
    {
        var response = context.Response;
        if (!OriginAllowed(context.Request))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!_pairBucket.TryTake())
        {
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        if (!IsJson(context.Request.ContentType))
        {
            response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        if (context.Request.ContentLength > MaxPairBodyBytes)
        {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var body = new byte[MaxPairBodyBytes + 1];
        var length = 0;
        while (length < body.Length)
        {
            var read = await context.Request.Body.ReadAsync(body.AsMemory(length), context.RequestAborted);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > MaxPairBodyBytes)
        {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        PairRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<PairRequest>(body.AsSpan(0, length), ContractJson.Options);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            request = null;
        }

        if (request is null || !ContractValidator.IsValid(request))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var result = await _o.Pairing.TryPairAsync(request);
        switch (result.Outcome)
        {
            case PairOutcome.Paired:
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = "application/json";
                response.Headers.CacheControl = "no-store";
                await response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(result.Response, ContractJson.Options), context.RequestAborted);
                break;
            case PairOutcome.NotOpen:
                response.StatusCode = StatusCodes.Status404NotFound;
                break;
            case PairOutcome.WrongCode:
                response.StatusCode = StatusCodes.Status403Forbidden;
                break;
            case PairOutcome.Gone:
                response.StatusCode = StatusCodes.Status410Gone;
                break;
            case PairOutcome.RegistryFull:
                response.StatusCode = StatusCodes.Status409Conflict;
                break;
            case PairOutcome.InvalidRequest:
                response.StatusCode = StatusCodes.Status400BadRequest;
                break;
            default:
                response.StatusCode = StatusCodes.Status500InternalServerError;
                break;
        }
    }
}
