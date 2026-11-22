const FIRST_DELAY_MS = 500;
const MAX_DELAY_MS = 5000;

/** Delay before reconnect attempt number `attempt` (0 = first retry): 0.5 s doubling up to 5 s. */
export function backoffDelay(attempt: number): number {
  const n = Math.max(0, Math.floor(attempt));
  return Math.min(FIRST_DELAY_MS * 2 ** Math.min(n, 10), MAX_DELAY_MS);
}
