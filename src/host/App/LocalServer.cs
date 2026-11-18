using System.Net;
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

    public static async Task<LocalServer> StartAsync(string clientDir, string editorDir)
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
            if (context.Request.Path.Equals("/editor", StringComparison.Ordinal))
            {
                context.Response.Redirect("/editor/");
                return;
            }
            await next();
        });

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
