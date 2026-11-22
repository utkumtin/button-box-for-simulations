/** Milliseconds left until an ISO date-time; never negative, 0 when the value cannot be parsed. */
export function remainingMs(expiresAt: string, now: number): number {
  const t = Date.parse(expiresAt);
  if (Number.isNaN(t)) return 0;
  return Math.max(0, t - now);
}

/** m:ss, rounded up to whole seconds so "0:00" only shows when the code has really expired. Never negative. */
export function formatCountdown(ms: number): string {
  const total = Math.ceil(Math.max(0, Number.isFinite(ms) ? ms : 0) / 1000);
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}:${s.toString().padStart(2, '0')}`;
}
