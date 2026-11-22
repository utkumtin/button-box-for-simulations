using System.Net;
using System.Text;
using ButtonBox.App.Lan;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ButtonBox.App.Pairing;

/// <summary>
/// The plain-HTTP page that exists only while the pairing wizard is open (decisions §9). It offers exactly two things:
/// the instructions at <c>/</c> and the CA certificate at <c>/ca.cer</c>. Nothing else is served and nothing here can pair.
/// The CA is public; the risk of fetching it over HTTP is closed by the user comparing the fingerprint on iOS's own screen.
/// </summary>
public sealed class PairingPageServer : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly WebApplication _app;
    private int _disposed;

    private PairingPageServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    /// <summary>The port of the first bound address (the only one unless port 0 was requested for several addresses).</summary>
    public int Port { get; }

    /// <param name="continueUrl">Where the "Continue" link points, e.g. https://buttonbox-7f3a.local/ (no user input).</param>
    /// <exception cref="IOException">Binding failed; see <see cref="PortConflict.IsAddressInUse"/>.</exception>
    public static async Task<PairingPageServer> StartAsync(IReadOnlyList<IPAddress> bindAddresses, int port, byte[] caCertificateDer, string continueUrl)
    {
        ArgumentNullException.ThrowIfNull(bindAddresses);
        ArgumentNullException.ThrowIfNull(caCertificateDer);
        ArgumentException.ThrowIfNullOrEmpty(continueUrl);
        if (bindAddresses.Count == 0)
        {
            throw new ArgumentException("At least one address is required.", nameof(bindAddresses));
        }

        var page = Encoding.UTF8.GetBytes(BuildPage(continueUrl));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = StopTimeout);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxConcurrentConnections = 32;
            o.Limits.MaxRequestBodySize = 0;
            o.Limits.MaxRequestHeadersTotalSize = 8 * 1024;
            o.Limits.MaxRequestHeaderCount = 40;
            o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            foreach (var address in bindAddresses)
            {
                o.Listen(address, port);
            }
        });

        var app = builder.Build();
        app.UseSecurityHeaders();
        app.Run(async context =>
        {
            var path = context.Request.Path;
            if (HttpMethods.IsGet(context.Request.Method) && path.Equals("/", StringComparison.Ordinal))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength = page.Length;
                await context.Response.Body.WriteAsync(page, context.RequestAborted);
            }
            else if (HttpMethods.IsGet(context.Request.Method) && path.Equals("/ca.cer", StringComparison.Ordinal))
            {
                // Same headers as the S1 spike, which iOS accepted as a profile download.
                context.Response.ContentType = "application/x-x509-ca-cert";
                context.Response.Headers.ContentDisposition = "attachment; filename=\"button-box-ca.crt\"";
                context.Response.ContentLength = caCertificateDer.Length;
                await context.Response.Body.WriteAsync(caCertificateDer, context.RequestAborted);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });

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
        return new PairingPageServer(app, new Uri(bound).Port);
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

    // No script and no inline style (the CSP forbids both); no fingerprint here: the comparison is iOS's screen against the PC's.
    private static string BuildPage(string continueUrl)
    {
        var url = WebUtility.HtmlEncode(continueUrl);
        return
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<meta name=\"format-detection\" content=\"telephone=no\">" +
            "<title>Button Box Setup</title></head><body>" +
            "<h1>Button Box - Setup</h1>" +
            "<ol>" +
            "<li><a href=\"/ca.cer\">Download Certificate</a> and tap Allow.</li>" +
            "<li>Open Settings and tap Profile Downloaded.</li>" +
            "<li>Before you tap Install, open More Details and compare the SHA-256 fingerprint with the one shown on your PC. Continue only if they are identical.</li>" +
            "<li>Install the profile.</li>" +
            "<li>Open Settings, General, About, Certificate Trust Settings and turn on full trust for the Button Box certificate.</li>" +
            "<li>Come back to this page and tap <a href=\"" + url + "\">Continue</a>.</li>" +
            "</ol></body></html>";
    }
}
