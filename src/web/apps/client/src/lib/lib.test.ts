import { describe, expect, it } from 'vitest';
import { backoffDelay } from './backoff';
import { closeAction } from './closeAction';
import { parseDevice } from './device';
import { pairErrorMessage } from './pairErrors';
import { buildScreenInfo, isPortrait } from './screen';

const TOKEN = 'A'.repeat(43);

describe('parseDevice', () => {
  it('accepts exactly what the host issues, so a paired phone stays paired', () => {
    expect(parseDevice(JSON.stringify({ deviceId: 'dev1', token: TOKEN }))).toEqual({
      deviceId: 'dev1',
      token: TOKEN,
    });
  });
  it('treats a missing entry as no device, so the pairing screen shows', () => {
    expect(parseDevice(null)).toBeNull();
  });
  it('treats corrupt JSON as no device instead of crashing at startup', () => {
    expect(parseDevice('{nope')).toBeNull();
  });
  it('rejects a wrong-shaped token so a bad value is never sent as hello', () => {
    expect(parseDevice(JSON.stringify({ deviceId: 'd', token: 'short' }))).toBeNull();
    expect(parseDevice(JSON.stringify({ deviceId: 'd', token: TOKEN + '=' }))).toBeNull();
    expect(parseDevice(JSON.stringify({ deviceId: '', token: TOKEN }))).toBeNull();
    expect(parseDevice(JSON.stringify({ token: TOKEN }))).toBeNull();
    expect(parseDevice('null')).toBeNull();
    expect(parseDevice('"x"')).toBeNull();
  });
});

describe('closeAction', () => {
  it('4401 forgets the token so the phone can re-pair after removal on the PC', () => {
    expect(closeAction(4401)).toBe('forget');
  });
  it('4409 stops retrying so two windows do not fight over the device', () => {
    expect(closeAction(4409)).toBe('stop');
  });
  it('every other code reconnects, so a flaky network heals itself', () => {
    for (const c of [1000, 1001, 1006, 1008, 1009, 4408, 4429]) expect(closeAction(c)).toBe('reconnect');
  });
});

describe('backoffDelay', () => {
  it('doubles from 0.5 s and caps at 5 s so a restarted PC is found quickly without hammering', () => {
    expect([0, 1, 2, 3, 4, 5, 50].map(backoffDelay)).toEqual([500, 1000, 2000, 4000, 5000, 5000, 5000]);
  });
});

describe('pairErrorMessage', () => {
  it('gives the user an actionable message per status', () => {
    expect(pairErrorMessage(403)).toBe('Wrong code');
    expect(pairErrorMessage(404)).toMatch(/Add Device/);
    expect(pairErrorMessage(410)).toMatch(/expired or was blocked/);
  });
  it('falls back to Try again for 429 and unknown statuses', () => {
    expect(pairErrorMessage(429)).toBe('Try again');
    expect(pairErrorMessage(500)).toBe('Try again');
  });
});

describe('buildScreenInfo', () => {
  const safeArea = { top: 0, right: 47, bottom: 21, left: 47 };
  it('rounds the viewport to integers because the host schema requires them', () => {
    const s = buildScreenInfo({ innerWidth: 852.4, innerHeight: 392.6, safeArea, devicePixelRatio: 3 });
    expect(s).toEqual({ width: 852, height: 393, safeArea, pixelRatio: 3 });
  });
  it('normalises to landscape so the generic template is built from width >= height', () => {
    const s = buildScreenInfo({ innerWidth: 393, innerHeight: 852, safeArea, devicePixelRatio: 3 });
    expect(s.width).toBe(852);
    expect(s.height).toBe(393);
  });
  it('does not clamp: invalid values reach the host, which is the authority', () => {
    const s = buildScreenInfo({ innerWidth: 10, innerHeight: 5, safeArea, devicePixelRatio: 9 });
    expect(s).toMatchObject({ width: 10, height: 5, pixelRatio: 9 });
  });
});

describe('isPortrait', () => {
  it('shows the rotate message only when taller than wide', () => {
    expect(isPortrait(393, 852)).toBe(true);
    expect(isPortrait(852, 393)).toBe(false);
    expect(isPortrait(500, 500)).toBe(false);
  });
});
