using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ButtonBox.App.Lan;

/// <summary>Response headers shared by every server that answers phones.</summary>
internal static class HttpSecurity
{
    public static void UseSecurityHeaders(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Content-Security-Policy"] = LocalServer.ContentSecurityPolicy;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                return Task.CompletedTask;
            });
            await next();
        });
    }
}
