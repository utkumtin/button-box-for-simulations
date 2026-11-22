using System.Globalization;
using System.Net;
using ButtonBox.App.Lan;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;

namespace ButtonBox.App.Pairing;

public sealed class PairingSessionOptions
{
    public required CertificateAuthority Authority { get; init; }

    public required DeviceRegistry Registry { get; init; }

    public required TemplateStore Templates { get; init; }

    /// <summary>The host label (<c>buttonbox-7f3a</c>); the shown address is <c>http://&lt;label&gt;.local</c>.</summary>
    public required string MdnsLabel { get; init; }

    /// <summary>The addresses the HTTP page binds to, read at each <see cref="PairingSession.StartAsync"/>. Empty means no LAN.</summary>
    public required Func<IReadOnlyList<IPAddress>> BindAddresses { get; init; }

    /// <summary>Port of the HTTPS server, used for the "Continue" link (omitted when 443).</summary>
    public int HttpsPort { get; init; } = 443;

    public int PrimaryPort { get; init; } = 80;

    public int FallbackPort { get; init; } = 8080;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// The whole pairing state machine (decisions §9, §8): the HTTP certificate page, the one-time code, the attempt lockout and
/// the creation of the device. The editor API only maps its endpoints onto this class; the phone's <c>/api/pair</c> maps onto
/// <see cref="TryPairAsync"/>. Thread-safe.
/// </summary>
public sealed class PairingSession : IAsyncDisposable
{
    private const int DefaultHttpPort = 80;

    private readonly PairingSessionOptions _o;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    private readonly ITimer _idle;
    private PairingState _state = PairingState.Closed;
    private PairingFailure? _failure;
    private PairingPageServer? _server;
    private string? _address;
    private string? _chosenTemplateId;
    private PairingCode? _code;
    private string? _deviceId;
    private bool _screenMismatch;
    private bool _disposed;

    public PairingSession(PairingSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options;
        _idle = options.Time.CreateTimer(static s => ((PairingSession)s!).OnIdle(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised after the state changed, on whatever thread caused it. Handlers must not throw or block.</summary>
    public event Action? Changed;

    /// <summary>A snapshot. A shown code that is past its expiry reads as failed/codeExpired even if nobody asked before.</summary>
    public PairingStatus Status
    {
        get
        {
            lock (_gate)
            {
                ExpireLocked();
                return SnapshotLocked();
            }
        }
    }

    /// <summary>
    /// Opens the wizard: starts the HTTP page on the primary port, or the fallback when it is taken. Restarts cleanly when
    /// already open. Both ports taken gives state failed/portInUse.
    /// </summary>
    /// <exception cref="ArgumentException">The template id is unknown (the editor API answers 400).</exception>
    /// <exception cref="InvalidOperationException">There is no LAN address to bind to.</exception>
    public async Task<PairingStatus> StartAsync(string? templateId)
    {
        if (templateId is not null && !_o.Templates.TryGet(templateId, out _))
        {
            throw new ArgumentException("Unknown template.", nameof(templateId));
        }

        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopServerAsync(resetState: true);

            var addresses = _o.BindAddresses();
            if (addresses.Count == 0)
            {
                throw new InvalidOperationException("There is no LAN address to serve the pairing page on.");
            }

            var continueUrl = "https://" + _o.MdnsLabel + ".local" + (_o.HttpsPort == 443 ? string.Empty : ":" + _o.HttpsPort.ToString(CultureInfo.InvariantCulture)) + "/";
            PairingPageServer? server = null;
            var port = 0;
            foreach (var candidate in new[] { _o.PrimaryPort, _o.FallbackPort })
            {
                try
                {
                    server = await PairingPageServer.StartAsync(addresses, candidate, _o.Authority.CertificateDer, continueUrl);
                    port = candidate;
                    break;
                }
                catch (Exception e) when (PortConflict.IsAddressInUse(e))
                {
                    // Try the next port.
                }
            }

            lock (_gate)
            {
                _chosenTemplateId = templateId;
                if (server is null)
                {
                    _state = PairingState.Failed;
                    _failure = PairingFailure.PortInUse;
                }
                else
                {
                    _server = server;
                    _state = PairingState.CaDownload;
                    _address = "http://" + _o.MdnsLabel + ".local" + (port == DefaultHttpPort ? string.Empty : ":" + port.ToString(CultureInfo.InvariantCulture));
                    ArmIdleLocked();
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        RaiseChanged();
        return Status;
    }

    /// <summary>
    /// Creates a new code. Allowed while the certificate page is up, while a code is shown, and after the code expired or
    /// was burned. In any other state it throws <see cref="InvalidOperationException"/> (the editor API answers 409).
    /// </summary>
    public PairingStatus ShowCode()
    {
        PairingStatus status;
        lock (_gate)
        {
            ExpireLocked();
            var allowed = _state is PairingState.CaDownload or PairingState.CodeShown
                || (_state == PairingState.Failed && _failure is PairingFailure.CodeExpired or PairingFailure.CodeBurned);
            if (!allowed)
            {
                throw new InvalidOperationException("A code cannot be shown in the current state.");
            }

            _code = PairingCode.Create(_o.Time);
            _failure = null;
            _state = PairingState.CodeShown;
            ArmIdleLocked();
            status = SnapshotLocked();
        }

        RaiseChanged();
        return status;
    }

    /// <summary>Closes the wizard: stops the HTTP page and forgets the code. Idempotent.</summary>
    public async Task<PairingStatus> CancelAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopServerAsync(resetState: true);
        }
        finally
        {
            _lifecycle.Release();
        }

        RaiseChanged();
        return Status;
    }

    /// <summary>
    /// The phone's attempt (<c>POST /api/pair</c>). The request must already be deserialized with the contract options; it is
    /// validated again here. On success the device exists, its token is in the result (shown to nobody else) and the HTTP page is stopped.
    /// </summary>
    public async Task<PairAttemptResult> TryPairAsync(PairRequest request)
    {
        if (!ContractValidator.IsValid(request))
        {
            return new PairAttemptResult(PairOutcome.InvalidRequest);
        }

        PairAttemptResult result;
        lock (_gate)
        {
            ExpireLocked();
            switch (_state)
            {
                case PairingState.Closed:
                    return new PairAttemptResult(PairOutcome.NotOpen);
                case PairingState.CaDownload:
                    // No code exists yet, so nothing is consumed and nothing can be guessed.
                    return new PairAttemptResult(PairOutcome.WrongCode);
                case PairingState.Paired:
                    return new PairAttemptResult(PairOutcome.Gone);
                case PairingState.Failed:
                    return new PairAttemptResult(_failure is PairingFailure.CodeExpired or PairingFailure.CodeBurned ? PairOutcome.Gone : PairOutcome.NotOpen);
            }

            switch (_code!.TryUse(request.Code))
            {
                case PairingCodeResult.Wrong:
                    if (_code.AttemptsLeft > 0)
                    {
                        result = new PairAttemptResult(PairOutcome.WrongCode);
                    }
                    else
                    {
                        // The last attempt burns the code for the whole session, whoever sent it.
                        _state = PairingState.Failed;
                        _failure = PairingFailure.CodeBurned;
                        result = new PairAttemptResult(PairOutcome.Gone);
                    }

                    break;
                case PairingCodeResult.Expired:
                    _state = PairingState.Failed;
                    _failure = PairingFailure.CodeExpired;
                    result = new PairAttemptResult(PairOutcome.Gone);
                    break;
                case PairingCodeResult.Burned:
                    _state = PairingState.Failed;
                    _failure = PairingFailure.CodeBurned;
                    result = new PairAttemptResult(PairOutcome.Gone);
                    break;
                case PairingCodeResult.AlreadyUsed:
                    result = new PairAttemptResult(PairOutcome.Gone);
                    break;
                default:
                    result = CreateDeviceLocked(request);
                    break;
            }
        }

        RaiseChanged();
        if (result.Outcome == PairOutcome.Paired)
        {
            // The pairing page is no longer needed; stopping it also closes the window in which the CA is offered.
            await _lifecycle.WaitAsync();
            try
            {
                await StopServerAsync(resetState: false);
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopServerAsync(resetState: true);
        }
        finally
        {
            _lifecycle.Release();
        }

        _idle.Dispose();
    }

    // The code was right and is spent. Whatever fails from here on leaves the session in failed/codeBurned (the code cannot be
    // reused; the wizard shows a new one), and the phone sees 409 or 500.
    private PairAttemptResult CreateDeviceLocked(PairRequest request)
    {
        try
        {
            DeviceTemplate? template;
            var mismatch = false;
            if (_chosenTemplateId is not null)
            {
                if (!_o.Templates.TryGet(_chosenTemplateId, out template))
                {
                    return BurnedLocked(PairOutcome.InternalError);
                }

                mismatch = TemplateStore.Mismatches(template, request.Screen);
            }
            else
            {
                template = _o.Templates.GetOrAddGeneric(request.Screen);
            }

            var name = template.Generic ? "Device" : template.Name;
            var (device, token) = _o.Registry.Add(name, template.Id, mismatch);
            _state = PairingState.Paired;
            _failure = null;
            _deviceId = device.Id;
            _screenMismatch = mismatch;
            _code = null;
            _idle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return new PairAttemptResult(PairOutcome.Paired, new PairResponse { DeviceId = device.Id, Token = token });
        }
        catch (DeviceRegistryFullException)
        {
            return BurnedLocked(PairOutcome.RegistryFull);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DeviceRegistryException or ArgumentException)
        {
            return BurnedLocked(PairOutcome.InternalError);
        }
    }

    private PairAttemptResult BurnedLocked(PairOutcome outcome)
    {
        _state = PairingState.Failed;
        _failure = PairingFailure.CodeBurned;
        return new PairAttemptResult(outcome);
    }

    // Callers hold _lifecycle. resetState: forget everything and go to closed; otherwise only the page goes away (after pairing).
    private async Task StopServerAsync(bool resetState)
    {
        PairingPageServer? server;
        lock (_gate)
        {
            if (!resetState && _state != PairingState.Paired)
            {
                return; // restarted or cancelled in the meantime: the page belongs to a newer session
            }

            server = _server;
            _server = null;
            if (resetState)
            {
                _failure = null;
                _address = null;
                _chosenTemplateId = null;
                _code = null;
                _deviceId = null;
                _screenMismatch = false;
                _state = PairingState.Closed;
                _idle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }

    private void ExpireLocked()
    {
        if (_state == PairingState.CodeShown && _code is not null && _o.Time.GetUtcNow() >= _code.ExpiresAt)
        {
            _state = PairingState.Failed;
            _failure = PairingFailure.CodeExpired;
        }
    }

    private void ArmIdleLocked() => _idle.Change(_o.IdleTimeout, Timeout.InfiniteTimeSpan);

    private PairingStatus SnapshotLocked()
    {
        var status = new PairingStatus { State = _state };
        switch (_state)
        {
            case PairingState.CaDownload:
            case PairingState.CodeShown:
            case PairingState.Failed:
                status.Address = _address;
                status.CaFingerprint = _address is null ? null : _o.Authority.Sha256Fingerprint;
                status.Failure = _failure;
                if (_state == PairingState.CodeShown && _code is not null)
                {
                    status.Code = _code.Code;
                    status.CodeExpiresAt = _code.ExpiresAt;
                    status.AttemptsLeft = _code.AttemptsLeft;
                }

                break;
            case PairingState.Paired:
                status.DeviceId = _deviceId;
                status.ScreenMismatch = _screenMismatch;
                break;
        }

        return status;
    }

    // Ten idle minutes: the wizard was left open and forgotten, so the CA download page goes away.
    private void OnIdle() => _ = CancelQuietlyAsync();

    private async Task CancelQuietlyAsync()
    {
        try
        {
            await CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Disposed meanwhile: already closed.
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A subscriber's failure must not break pairing.
        }
    }
}
