// WebSocket close codes the host sends to the phone. The same values live in
// src/host/Core/Contracts/CloseCodes.cs; keep both in sync.
export const CloseCodes = {
  /** The message broke the protocol (bad JSON, unknown type, failed validation). */
  BadMessage: 1008,
  /** A frame was larger than the limit. */
  TooBig: 1009,
  /** The token is unknown or revoked: delete the token and show the pairing screen. */
  TokenRejected: 4401,
  /** No hello arrived in time. */
  HelloTimeout: 4408,
  /** A newer connection of the same device replaced this one: do not reconnect automatically. */
  Replaced: 4409,
  /** Too many messages. */
  RateLimited: 4429,
} as const;
