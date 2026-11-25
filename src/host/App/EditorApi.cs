using System.Text.Json;
using ButtonBox.App.Lan;
using ButtonBox.App.Pairing;
using ButtonBox.App.Profiles;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Output.Scs;
using ButtonBox.Output.Scs.Install;
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

    // The Phase 4 parts. Each is null while it does not exist (and in the older tests); its routes then answer 503.
    public ProfileStore? Profiles { get; init; }

    public CatalogStore? Catalogs { get; init; }

    public AssetLibrary? Assets { get; init; }

    /// <summary>Test mode sends through it.</summary>
    public GameSession? Session { get; init; }

    /// <summary>The live field list of the Telemetry page.</summary>
    public IStateProvider? Telemetry { get; init; }

    /// <summary>SHA-256 of the CA certificate, or null when there is no usable CA.</summary>
    public Func<string?>? CaFingerprint { get; init; }

    public IFirewallStatus? Firewall { get; init; }

    /// <summary>Regenerates the CA (see <see cref="LanHost.RegenerateCertificateAuthorityAsync"/>).</summary>
    public Func<Task<CaRegenerationResult>>? RegenerateCa { get; init; }

    /// <summary>Installs the SCS plugin and reports its state per game. Null: the settings say "not included" and the install route answers 503.</summary>
    public IScsPluginInstaller? ScsInstaller { get; init; }

    /// <summary>The bridge's link state, for the "connected" flags. Null (or returning null) means no bridge, as in simulator mode.</summary>
    public Func<ScsBridgeStatus?>? ScsLink { get; init; }
}

/// <summary>
/// The editor API under <c>/editor/api</c>, on the loopback server only (decisions §3, §9). The Host check and the OPTIONS
/// refusal happen before this runs (see <see cref="LocalServer"/>). Every request needs the session cookie; every POST also
/// needs a matching Origin and a JSON content type. Nothing here ever logs or echoes the secret.
/// </summary>
public static partial class EditorApi
{
    public const string CookieName = "bbs_editor";
    public const int MaxBodyBytes = 4096;
    public const int MaxProfileBytes = 1024 * 1024;
    public const int MaxImageBytes = 4 * 1024 * 1024;

    // A copy of the contract options with a small depth cap: a hostile body cannot nest deeper than any real profile.
    private static readonly JsonSerializerOptions BoundedJson = new(ContractJson.Options) { MaxDepth = 32 };

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

            var upload = Resolve(request.Path.Value ?? string.Empty, isPost).Route == Route.AssetUpload;
            if (upload ? !IsImage(request.ContentType) : !IsJson(request.ContentType))
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
        var (route, id) = Resolve(context.Request.Path.Value ?? string.Empty, isPost);
        switch (route)
        {
            case Route.NotFound:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            case Route.WrongMethod:
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            case Route.Status:
                await WriteJsonAsync(context, o.Status());
                break;
            case Route.Devices:
                await DevicesAsync(context, o);
                break;
            case Route.Templates:
                await WriteJsonAsync(context, new TemplateList { Templates = o.Templates.All });
                break;
            case Route.Pairing:
                await PairingAsync(context, o, static (p, _) => Task.FromResult(p.Status), needsBody: false);
                break;
            case Route.PairingStart:
                // Pairing without a running HTTPS server (443 taken, no CA, …) would hand the phone a certificate page
                // whose "Continue" leads nowhere; the editor banner already explains the cause.
                if (o.Status().LanServer != LanServerState.Running)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    break;
                }

                await PairingAsync(context, o, StartPairingAsync, needsBody: true);
                break;
            case Route.PairingCode:
                await PairingAsync(context, o, static (p, _) => Task.FromResult(p.ShowCode()), needsBody: false, conflictIsInvalidOperation: true);
                break;
            case Route.PairingCancel:
                await PairingAsync(context, o, static (p, _) => p.CancelAsync(), needsBody: false);
                break;
            case Route.DeviceRename:
                await RenameAsync(context, o, id!);
                break;
            case Route.DeviceRevoke:
                await RevokeAsync(context, o, id!);
                break;
            default:
                await RoutePhase4Async(context, o, route, id);
                break;
        }
    }

    private enum Route
    {
        NotFound,
        WrongMethod,
        Status,
        Devices,
        Templates,
        Pairing,
        PairingStart,
        PairingCode,
        PairingCancel,
        DeviceRename,
        DeviceRevoke,
        Catalogs,
        Profiles,
        ProfileGet,
        ProfileExport,
        ProfileCreate,
        ProfileImport,
        ProfileSave,
        ProfileDuplicate,
        ProfileDelete,
        ProfileActivate,
        Assets,
        AssetUpload,
        AssetGet,
        AssetDelete,
        Telemetry,
        Test,
        Settings,
        RegenerateCa,
        ScsInstall,
    }

    /// <summary>
    /// The route table: path and method to a route and its id. A path that exists for another method is <see cref="Route.WrongMethod"/>.
    /// An id is only text here; its handler checks it against the id pattern before any store call.
    /// </summary>
    private static (Route Route, string? Id) Resolve(string path, bool isPost)
    {
        var s = path.Split('/'); // "/devices/x/rename" -> "", "devices", "x", "rename"; stray or trailing slashes give an empty part
        if (s.Length < 2 || s.Length > 4 || s.Skip(1).Any(string.IsNullOrEmpty))
        {
            return (Route.NotFound, null);
        }

        (Route Route, string? Id, bool Post)? hit = s.Length switch
        {
            2 => s[1] switch
            {
                "status" => (Route.Status, null, false),
                "devices" => (Route.Devices, null, false),
                "templates" => (Route.Templates, null, false),
                "pairing" => (Route.Pairing, null, false),
                "catalogs" => (Route.Catalogs, null, false),
                "profiles" => (Route.Profiles, null, false),
                "assets" => (Route.Assets, null, false),
                "telemetry" => (Route.Telemetry, null, false),
                "settings" => (Route.Settings, null, false),
                "test" => (Route.Test, null, true),
                _ => null,
            },
            3 => (s[1], s[2]) switch
            {
                ("pairing", "start") => (Route.PairingStart, null, true),
                ("pairing", "code") => (Route.PairingCode, null, true),
                ("pairing", "cancel") => (Route.PairingCancel, null, true),
                ("profiles", "create") when isPost => (Route.ProfileCreate, null, true),
                ("profiles", "import") when isPost => (Route.ProfileImport, null, true),
                ("profiles", _) => (Route.ProfileGet, s[2], false),
                ("assets", "upload") => (Route.AssetUpload, null, true),
                ("assets", _) => (Route.AssetGet, s[2], false),
                ("settings", "regenerate-ca") => (Route.RegenerateCa, null, true),
                _ => null,
            },
            _ => (s[1], s[3]) switch
            {
                ("devices", "rename") => (Route.DeviceRename, s[2], true),
                ("devices", "revoke") => (Route.DeviceRevoke, s[2], true),
                ("profiles", "export") => (Route.ProfileExport, s[2], false),
                ("profiles", "save") => (Route.ProfileSave, s[2], true),
                ("profiles", "duplicate") => (Route.ProfileDuplicate, s[2], true),
                ("profiles", "delete") => (Route.ProfileDelete, s[2], true),
                ("profiles", "activate") => (Route.ProfileActivate, s[2], true),
                ("settings", "install") when s[2] == "scs-plugin" => (Route.ScsInstall, null, true),
                ("assets", "delete") => (Route.AssetDelete, s[2], true),
                _ => null,
            },
        };

        return hit switch
        {
            null => (Route.NotFound, null),
            { } h when h.Post != isPost => (Route.WrongMethod, null),
            { } h => (h.Route, h.Id),
        };
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

    /// <summary>Exactly <c>image/png</c> or <c>image/webp</c>, no parameters: what an image upload is.</summary>
    private static bool IsImage(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && parsed.Parameters.Count == 0
        && (parsed.MediaType.Equals("image/png", StringComparison.OrdinalIgnoreCase) || parsed.MediaType.Equals("image/webp", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads at most <paramref name="max"/> bytes. Answers 413 (or 400 when the client broke off) and returns null otherwise.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext context, int max = MaxBodyBytes)
    {
        var request = context.Request;
        if (request.ContentLength > max)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return null;
        }

        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            // Chunked bodies have no Content-Length, so the cap is enforced on what actually arrives.
            while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (buffer.Length + read > max)
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
            return JsonSerializer.Deserialize<T>(body, BoundedJson);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task WriteJsonAsync<T>(HttpContext context, T value, int status = StatusCodes.Status200OK)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private sealed class BadRequestException : Exception;
}
