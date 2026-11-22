using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ButtonBox.App;

/// <summary>Serves the phone client at / and the editor at /editor/ on loopback only.</summary>
public sealed class LocalServer : IAsyncDisposable
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

    private readonly WebApplication _app;

    private LocalServer(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>Resolved base address, e.g. http://127.0.0.1:54321/.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The only Host this server answers to. Anything else (DNS rebinding, another name for loopback) gets 400.</summary>
    internal static string HostFor(HttpContext context) => "127.0.0.1:" + context.Connection.LocalPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>The only Origin the editor API accepts on a POST.</summary>
    internal static string OriginFor(HttpContext context) => "http://" + HostFor(context);

    /// <param name="editorApi">When set, <c>/editor/api</c> is served. The API exists on this loopback server only, never on the LAN one.</param>
    public static async Task<LocalServer> StartAsync(string clientDir, string editorDir, EditorApiOptions? editorApi = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Listen(IPAddress.Loopback, 0);
        });

        var app = builder.Build();
        var client = ProviderFor(clientDir);
        var editor = ProviderFor(editorDir);

        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Content-Security-Policy"] = ContentSecurityPolicy;
                headers["X-Content-Type-Options"] = "nosniff";
                return Task.CompletedTask;
            });
            await next();
        });

        app.Use(async (context, next) =>
        {
            if (!string.Equals(context.Request.Host.Value, HostFor(context), StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // No CORS anywhere: no preflight is ever answered, and no Access-Control-* header is ever sent.
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            await next();
        });

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals("/editor", StringComparison.Ordinal))
            {
                context.Response.Redirect("/editor/");
                return;
            }
            await next();
        });

        if (editorApi is not null)
        {
            // Registered before "/editor" so the API never falls through to static files; Map matches case-insensitively.
            app.Map("/editor/api", branch => branch.Run(context => EditorApi.HandleAsync(context, editorApi)));
        }

        app.Map("/editor", branch => UseStatic(branch, editor));
        UseStatic(app, client);

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new LocalServer(app, new Uri(address.EndsWith('/') ? address : address + "/"));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static IFileProvider ProviderFor(string dir) =>
        Directory.Exists(dir) ? new PhysicalFileProvider(Path.GetFullPath(dir)) : new NullFileProvider();

    private static void UseStatic(IApplicationBuilder app, IFileProvider files)
    {
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    }
}
