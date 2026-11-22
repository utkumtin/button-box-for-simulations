using System.Text.Json;
using ButtonBox.App.Pairing;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace ButtonBox.App;

/// <summary>What the editor API reads and changes. Narrow on purpose: the loopback server can start before the LAN side exists.</summary>
public sealed class EditorApiOptions
{
    public required EditorSecret Secret { get; init; }

    /// <summary>Null when devices.json could not be loaded; the device routes then answer 503.</summary>
    public DeviceRegistry? Registry { get; init; }

    public required TemplateStore Templates { get; init; }

    /// <summary>Null while the LAN side does not exist (not started, or no certificate authority); the pairing routes then answer 503.</summary>
    public required Func<PairingSession?> Pairing { get; init; }

    public required Func<string, bool> IsConnected { get; init; }

    public required Func<HostStatus> Status { get; init; }
}

/// <summary>
/// The editor API under <c>/editor/api</c>, on the loopback server only (decisions §3, §9). The Host check and the OPTIONS
/// refusal happen before this runs (see <see cref="LocalServer"/>). Every request needs the session cookie; every POST also
/// needs a matching Origin and a JSON content type. Nothing here ever logs or echoes the secret.
/// </summary>
public static class EditorApi
{
    public const string CookieName = "bbs_editor";
    public const int MaxBodyBytes = 4096;

    public static async Task HandleAsync(HttpContext context, EditorApiOptions o)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store"; // the pairing status carries the code; also on 401/403/415

        if (!o.Secret.Matches(request.Cookies[CookieName]))
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var isPost = HttpMethods.IsPost(request.Method);
        if (!isPost && !HttpMethods.IsGet(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        if (isPost)
        {
            // A cross-site page cannot set Origin to ours, and a form cannot send JSON.
            var origin = request.Headers.Origin;
            if (origin.Count != 1 || !string.Equals(origin[0], LocalServer.OriginFor(context), StringComparison.Ordinal))
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!IsJson(request.ContentType))
            {
                response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }
        }

        try
        {
            await RouteAsync(context, o, isPost);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // devices.json could not be written; the registry keeps its old state.
            if (!response.HasStarted)
            {
                response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }
    }

    private static async Task RouteAsync(HttpContext context, EditorApiOptions o, bool isPost)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var segments = path.Split('/'); // "/devices/x/rename" -> "", "devices", "x", "rename"; stray or trailing slashes do not match
        string? id = null;
        string? route = null;
        if (segments.Length == 2)
        {
            route = segments[1];
            route = route is "status" or "devices" or "templates" or "pairing" ? route : null;
        }
        else if (segments.Length == 3 && segments[1] == "pairing" && segments[2] is "start" or "code" or "cancel")
        {
            route = "pairing/" + segments[2];
        }
        else if (segments.Length == 4 && segments[1] == "devices" && segments[3] is "rename" or "revoke")
        {
            id = segments[2];
            route = "devices/" + segments[3];
        }

        if (route is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var wantsPost = route.Contains('/');
        if (wantsPost != isPost)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        switch (route)
        {
            case "status":
                await WriteJsonAsync(context, o.Status());
                break;
            case "devices":
                await DevicesAsync(context, o);
                break;
            case "templates":
                await WriteJsonAsync(context, new TemplateList { Templates = o.Templates.All });
                break;
            case "pairing":
                await PairingAsync(context, o, static (p, _) => Task.FromResult(p.Status), needsBody: false);
                break;
            case "pairing/start":
                // Pairing without a running HTTPS server (443 taken, no CA, …) would hand the phone a certificate page
                // whose "Continue" leads nowhere; the editor banner already explains the cause.
                if (o.Status().LanServer != LanServerState.Running)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    break;
                }

                await PairingAsync(context, o, StartPairingAsync, needsBody: true);
                break;
            case "pairing/code":
                await PairingAsync(context, o, static (p, _) => Task.FromResult(p.ShowCode()), needsBody: false, conflictIsInvalidOperation: true);
                break;
            case "pairing/cancel":
                await PairingAsync(context, o, static (p, _) => p.CancelAsync(), needsBody: false);
                break;
            case "devices/rename":
                await RenameAsync(context, o, id!);
                break;
            default: // devices/revoke
                await RevokeAsync(context, o, id!);
                break;
        }
    }

    private static async Task DevicesAsync(HttpContext context, EditorApiOptions o)
    {
        if (o.Registry is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var devices = o.Registry.Devices.Select(d => new DeviceInfo
        {
            Id = d.Id,
            Name = d.Name,
            TemplateId = d.TemplateId,
            Connected = o.IsConnected(d.Id),
            ScreenMismatch = d.ScreenMismatch,
        }).ToList();
        await WriteJsonAsync(context, new DeviceList { Devices = devices });
    }

    private static async Task RenameAsync(HttpContext context, EditorApiOptions o, string id)
    {
        if (!ContractValidator.IsValidId(id))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (o.Registry is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var body = await ReadBodyAsync(context);
        if (body is null)
        {
            return;
        }

        var rename = Parse<RenameDeviceRequest>(body);
        if (rename is null || !ContractValidator.IsValid(rename))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        context.Response.StatusCode = o.Registry.Rename(id, rename.Name)
            ? StatusCodes.Status204NoContent
            : StatusCodes.Status404NotFound;
    }

    private static async Task RevokeAsync(HttpContext context, EditorApiOptions o, string id)
    {
        if (!ContractValidator.IsValidId(id))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (o.Registry is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (await ReadBodyAsync(context) is null)
        {
            return;
        }

        context.Response.StatusCode = o.Registry.Revoke(id)
            ? StatusCodes.Status204NoContent
            : StatusCodes.Status404NotFound;
    }

    private static async Task<PairingStatus> StartPairingAsync(PairingSession pairing, byte[]? body)
    {
        var start = Parse<StartPairingRequest>(body!);
        if (start is null || !ContractValidator.IsValid(start))
        {
            throw new BadRequestException();
        }

        try
        {
            return await pairing.StartAsync(start.TemplateId);
        }
        catch (ArgumentException)
        {
            throw new BadRequestException(); // unknown template id
        }
    }

    private static async Task PairingAsync(
        HttpContext context,
        EditorApiOptions o,
        Func<PairingSession, byte[]?, Task<PairingStatus>> action,
        bool needsBody,
        bool conflictIsInvalidOperation = false)
    {
        var pairing = o.Pairing();
        if (pairing is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        byte[]? body = null;
        if (context.Request.Method == HttpMethods.Post)
        {
            body = await ReadBodyAsync(context);
            if (body is null)
            {
                return;
            }
        }

        try
        {
            await WriteJsonAsync(context, await action(pairing, needsBody ? body : null));
        }
        catch (BadRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
        catch (ObjectDisposedException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; // shutting down
        }
        catch (InvalidOperationException)
        {
            // ShowCode in the wrong state is a conflict; StartAsync without a LAN address means the LAN side is unavailable.
            context.Response.StatusCode = conflictIsInvalidOperation
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status503ServiceUnavailable;
        }
    }

    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && parsed.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        && (!parsed.Charset.HasValue || parsed.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads at most <see cref="MaxBodyBytes"/>. Answers 413 (or 400 when the client broke off) and returns null otherwise.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength > MaxBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return null;
        }

        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[1024];
            int read;
            // Chunked bodies have no Content-Length, so the cap is enforced on what actually arrives.
            while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (buffer.Length + read > MaxBodyBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (Exception e) when (e is IOException or BadHttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return null;
        }
    }

    private static T? Parse<T>(byte[] body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, ContractJson.Options);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task WriteJsonAsync<T>(HttpContext context, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private sealed class BadRequestException : Exception;
}
