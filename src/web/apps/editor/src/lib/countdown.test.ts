import { describe, expect, it } from 'vitest';
import { formatCountdown, remainingMs } from './countdown';

describe('countdown', () => {
  // The code lives 2 minutes; the user must see "2:00" at the start, not "1:59".
  it('formats whole and partial seconds as m:ss, rounding up', () => {
    expect(formatCountdown(120_000)).toBe('2:00');
    expect(formatCountdown(119_100)).toBe('2:00');
    expect(formatCountdown(65_000)).toBe('1:05');
    expect(formatCountdown(1)).toBe('0:01');
    expect(formatCountdown(0)).toBe('0:00');
  });

  // A clock skew or late poll must not show "-1:-5".
  it('never goes negative', () => {
    expect(formatCountdown(-5000)).toBe('0:00');
    expect(formatCountdown(Number.NaN)).toBe('0:00');
    expect(remainingMs('2026-11-18T10:02:00+00:00', Date.parse('2026-11-18T10:05:00Z'))).toBe(0);
  });

  // The host sends an offset date-time; the timer must honour the offset.
  it('computes the remaining time from an offset ISO date-time', () => {
    expect(remainingMs('2026-11-18T12:02:00+02:00', Date.parse('2026-11-18T10:00:30Z'))).toBe(90_000);
    expect(remainingMs('garbage', 0)).toBe(0);
  });
});
