namespace ButtonBox.Core.Contracts;

/// <summary>
/// WebSocket close codes the host sends to the phone. The same values live in
/// <c>src/web/packages/protocol/src/closeCodes.ts</c>; keep both in sync.
/// </summary>
public static class CloseCodes
{
    /// <summary>The message broke the protocol (bad JSON, unknown type, failed validation).</summary>
    public const int BadMessage = 1008;

    /// <summary>A frame was larger than the limit.</summary>
    public const int TooBig = 1009;

    /// <summary>
    /// The token is unknown or revoked. The phone deletes its token and shows the pairing screen.
    /// Sent only when the registry definitively has no such device, never on an internal error.
    /// </summary>
    public const int TokenRejected = 4401;

    /// <summary>No <c>hello</c> arrived in time.</summary>
    public const int HelloTimeout = 4408;

    /// <summary>A newer connection of the same device replaced this one. The phone does not reconnect on its own.</summary>
    public const int Replaced = 4409;

    /// <summary>The device sent more messages than the rate limit allows.</summary>
    public const int RateLimited = 4429;
}
