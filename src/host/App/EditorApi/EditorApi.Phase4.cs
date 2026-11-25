using ButtonBox.App.Lan;
using ButtonBox.App.Profiles;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Output.Scs.Install;
using ButtonBox.Security;
using Microsoft.AspNetCore.Http;

namespace ButtonBox.App;

// The Phase 4 routes (decisions §6, §7, §11): profiles, images, telemetry, test mode, settings. The cookie, Host, Origin and
// content-type checks already ran in HandleAsync. Every id from the path is checked against its pattern before any store call.
public static partial class EditorApi
{
    /// <summary>The word the user types to confirm a CA regeneration.</summary>
    public const string RegenerateConfirmation = "REGENERATE";

    private const int MaxTelemetryFields = 512;

    private static async Task RoutePhase4Async(HttpContext context, EditorApiOptions o, Route route, string? id)
    {
        var response = context.Response;
        switch (route)
        {
            case Route.Catalogs:
                if (o.Catalogs is null)
                {
                    response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }

                await WriteJsonAsync(context, new CatalogList { Catalogs = o.Catalogs.All });
                return;
            case Route.Telemetry:
                await TelemetryAsync(context, o);
                return;
            case Route.Settings:
                await SettingsAsync(context, o);
                return;
            case Route.Test:
                await TestAsync(context, o);
                return;
            case Route.RegenerateCa:
                await RegenerateCaAsync(context, o);
                return;
            case Route.ScsInstall:
                await ScsInstallAsync(context, o);
                return;
            case Route.Assets or Route.AssetUpload or Route.AssetGet or Route.AssetDelete:
                await AssetsAsync(context, o, route, id);
                return;
            default:
                await ProfilesAsync(context, o, route, id);
                return;
        }
    }

    // ---- profiles ----

    private static async Task ProfilesAsync(HttpContext context, EditorApiOptions o, Route route, string? id)
    {
        var response = context.Response;
        if (o.Profiles is not { } store)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (id is not null && !ContractValidator.IsValidId(id))
        {
            response.StatusCode = StatusCodes.Status404NotFound; // before the body is read and before any file is touched
            return;
        }

        switch (route)
        {
            case Route.Profiles:
                await WriteJsonAsync(context, store.List());
                return;
            case Route.ProfileGet:
                await WriteResultAsync(context, store.Get(id!));
                return;
            case Route.ProfileExport:
                {
                    var result = store.Get(id!);
                    if (result.Ok)
                    {
                        response.Headers.ContentDisposition = $"attachment; filename=\"{id}.json\""; // the id matched the id pattern
                    }

                    await WriteResultAsync(context, result);
                    return;
                }

            case Route.ProfileCreate:
                {
                    var body = await ReadBodyAsync(context);
                    if (body is null)
                    {
                        return;
                    }

                    if (Parse<CreateProfileRequest>(body) is not { Name: not null } request)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }

                    await WriteResultAsync(context, store.Create(request.Name, request.Game));
                    return;
                }

            case Route.ProfileImport:
                {
                    var body = await ReadBodyAsync(context, MaxProfileBytes);
                    if (body is null)
                    {
                        return;
                    }

                    await WriteResultAsync(context, store.Import(body));
                    return;
                }

            case Route.ProfileSave:
                await SaveAsync(context, store, id!);
                return;
            default:
                break; // the three that only need an id
        }

        if (await ReadBodyAsync(context) is null)
        {
            return;
        }

        switch (route)
        {
            case Route.ProfileDuplicate:
                await WriteResultAsync(context, store.Duplicate(id!));
                return;
            case Route.ProfileDelete:
                await WriteResultAsync(context, store.Delete(id!), noBody: true);
                return;
            default: // activate
                await WriteResultAsync(context, store.Activate(id!), noBody: true);
                return;
        }
    }

    private static async Task SaveAsync(HttpContext context, ProfileStore store, string id)
    {
        var body = await ReadBodyAsync(context, MaxProfileBytes);
        if (body is null)
        {
            return;
        }

        var response = context.Response;
        if (Parse<Profile>(body) is not { } profile)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Conflicts the store reports as "invalid"; the editor needs to tell them from a profile that fails the checks.
        if (!string.Equals(profile.Id, id, StringComparison.Ordinal))
        {
            response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        var stored = store.Get(id);
        if (!stored.Ok)
        {
            await WriteResultAsync(context, stored, noBody: true); // 404 or 409 (damaged)
            return;
        }

        if (stored.Value!.Game != profile.Game)
        {
            response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        await WriteResultAsync(context, store.Save(id, profile), noBody: true);
    }

    private static async Task WriteResultAsync<T>(HttpContext context, StoreResult<T> result, bool noBody = false)
    {
        var response = context.Response;
        switch (result.Status)
        {
            case StoreStatus.Ok when noBody:
                response.StatusCode = StatusCodes.Status204NoContent;
                return;
            case StoreStatus.Ok:
                await WriteJsonAsync(context, result.Value!);
                return;
            case StoreStatus.NotFound:
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            case StoreStatus.Conflict:
                response.StatusCode = StatusCodes.Status409Conflict;
                return;
            case StoreStatus.Invalid:
                await WriteJsonAsync(context, new ProblemList { Problems = result.Problems }, StatusCodes.Status422UnprocessableEntity);
                return;
            default:
                response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
        }
    }

    // ---- images ----

    private static async Task AssetsAsync(HttpContext context, EditorApiOptions o, Route route, string? id)
    {
        var response = context.Response;
        if (o.Assets is not { } library || o.Profiles is not { } profiles)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (id is not null && !ProfileValidator.IsValidAssetId(id))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        switch (route)
        {
            case Route.Assets:
                {
                    var usage = profiles.AssetUsage();
                    await WriteJsonAsync(context, new AssetList
                    {
                        Assets = [.. library.List().Select(a => new AssetInfo
                    {
                        Id = a.Id,
                        Type = a.Type,
                        Size = a.Size,
                        Bytes = a.Bytes,
                        UsedBy = usage.TryGetValue(a.Id, out var users) ? users : [],
                    })],
                    });
                    return;
                }

            case Route.AssetGet:
                {
                    if (!library.TryGet(id, out var type, out var bytes))
                    {
                        response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    response.StatusCode = StatusCodes.Status200OK;
                    response.ContentType = type == AssetType.Png ? "image/png" : "image/webp";
                    response.Headers.XContentTypeOptions = "nosniff";
                    response.ContentLength = bytes.Length;
                    await response.Body.WriteAsync(bytes, context.RequestAborted);
                    return;
                }

            case Route.AssetUpload:
                {
                    var body = await ReadBodyAsync(context, MaxImageBytes);
                    if (body is null)
                    {
                        return;
                    }

                    // The type is decided by the bytes (decisions §7), not by the header the client sent.
                    var imported = library.Import(body);
                    if (imported.Asset is not { } asset)
                    {
                        await WriteJsonAsync(
                            context,
                            new ProblemList { Problems = [imported.Problem ?? "The image cannot be imported."] },
                            StatusCodes.Status422UnprocessableEntity);
                        return;
                    }

                    await WriteJsonAsync(context, new AssetInfo
                    {
                        Id = asset.Id,
                        Type = asset.Type,
                        Size = asset.Size,
                        Bytes = asset.Bytes,
                        UsedBy = profiles.ProfilesUsing(asset.Id),
                    });
                    return;
                }

            default: // delete
                {
                    if (await ReadBodyAsync(context) is null)
                    {
                        return;
                    }

                    if (!library.Contains(id))
                    {
                        response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    if (profiles.ProfilesUsing(id!).Count > 0)
                    {
                        response.StatusCode = StatusCodes.Status409Conflict; // decisions §7: an image a profile uses is never removed
                        return;
                    }

                    response.StatusCode = library.Delete(id) ? StatusCodes.Status204NoContent : StatusCodes.Status500InternalServerError;
                    return;
                }
        }
    }

    // ---- telemetry, test mode, settings ----

    private static async Task TelemetryAsync(HttpContext context, EditorApiOptions o)
    {
        if (o.Telemetry is not { } provider)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var snapshot = provider.Current;
        var fields = snapshot.Fields
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Take(MaxTelemetryFields)
            .Select(f => new TelemetryFieldValue { Name = f.Key, Value = f.Value })
            .ToList();
        await WriteJsonAsync(context, new TelemetryView { Available = snapshot.Available, GameListening = snapshot.GameListening, Fields = fields });
    }

    private static async Task TestAsync(HttpContext context, EditorApiOptions o)
    {
        var response = context.Response;
        var body = await ReadBodyAsync(context);
        if (body is null)
        {
            return;
        }

        if (Parse<TestPressRequest>(body) is not { ActionId: not null } request || !ContractValidator.IsValidId(request.ActionId))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (o.Session is not { } session)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        response.StatusCode = session.TestPress(request.ActionId) switch
        {
            TestPressResult.Sent => StatusCodes.Status204NoContent,
            TestPressResult.NoActiveProfile => StatusCodes.Status409Conflict,
            TestPressResult.UnknownAction => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status503ServiceUnavailable, // the output queue is full
        };
    }

    private static async Task SettingsAsync(HttpContext context, EditorApiOptions o)
    {
        if (o.CaFingerprint is not { } fingerprint || o.Firewall is not { } firewall)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var print = fingerprint();
        await WriteJsonAsync(context, new SettingsStatus
        {
            Certificate = new CertificateStatus { Available = print is not null, Fingerprint = print },
            Firewall = await firewall.GetAsync(),
            ScsPlugin = await Task.Run(() => ScsPluginStatusOf(o)), // reads Steam files and hashes the DLLs
        });
    }

    private static readonly ScsGame[] ScsGames = [ScsGame.Ets2, ScsGame.Ats];

    // Both games, ETS2 first. "Game not found" comes from the locator (through the installer), never from a server-check failure;
    // "connected" is the bridge's link and only for the game its hello named. A plugin of another protocol has another DLL than the
    // shipped one, so the installer already says updateAvailable.
    private static ScsPluginStatus ScsPluginStatusOf(EditorApiOptions o)
    {
        var installer = o.ScsInstaller;
        var link = o.ScsLink?.Invoke();
        return new ScsPluginStatus
        {
            Included = installer?.Included ?? false,
            Games =
            [
                .. ScsGames.Select(game =>
                {
                    var status = installer?.Status(game) ?? new PluginStatus(ScsPluginState.GameNotFound, null);
                    return new ScsGameStatus
                    {
                        Game = game,
                        State = status.State,
                        InstallDir = status.InstallDir,
                        Connected = link is { Connected: true, Game: { } linked } && linked == game,
                    };
                }),
            ],
        };
    }

    private static async Task ScsInstallAsync(HttpContext context, EditorApiOptions o)
    {
        var response = context.Response;
        if (o.ScsInstaller is not { } installer)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var body = await ReadBodyAsync(context);
        if (body is null)
        {
            return;
        }

        if (Parse<ScsInstallRequest>(body) is not { } request)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Copies a few MiB and hashes them; the installer serializes concurrent installs itself.
        var result = await Task.Run(() => installer.Install(request.Game));
        switch (result.Outcome)
        {
            case PluginInstallOutcome.Installed:
                response.StatusCode = StatusCodes.Status204NoContent;
                return;
            case PluginInstallOutcome.GameNotFound:
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            case PluginInstallOutcome.NotIncluded:
                response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            case PluginInstallOutcome.GameRunning:
                await WriteJsonAsync(
                    context,
                    new ProblemList { Problems = [$"Close {(request.Game == ScsGame.Ets2 ? "Euro Truck Simulator 2" : "American Truck Simulator")} first."] },
                    StatusCodes.Status409Conflict);
                return;
            default:
                await WriteJsonAsync(
                    context,
                    new ProblemList { Problems = [result.Reason ?? "The plugin could not be installed."] },
                    StatusCodes.Status422UnprocessableEntity);
                return;
        }
    }

    private static async Task RegenerateCaAsync(HttpContext context, EditorApiOptions o)
    {
        var response = context.Response;
        var body = await ReadBodyAsync(context);
        if (body is null)
        {
            return;
        }

        // The typed word is checked before anything else: a wrong word never reaches the CA, whatever state it is in.
        if (Parse<RegenerateCaRequest>(body) is not { Confirm: not null } request
            || !string.Equals(request.Confirm, RegenerateConfirmation, StringComparison.Ordinal))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (o.RegenerateCa is not { } regenerate)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // Not tied to the request's cancellation: once it started, the regeneration runs to its commit or its rollback.
        response.StatusCode = await regenerate() switch
        {
            CaRegenerationResult.Done => StatusCodes.Status204NoContent,
            CaRegenerationResult.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };
    }
}
