import { describe, expect, it } from 'vitest';
import { groupFingerprint } from './fingerprint';

const HEX = '26EC54A3' + '00'.repeat(24) + 'DEADBEEF';

describe('groupFingerprint', () => {
  // The user compares against iOS, which shows one space-separated byte per group; any other grouping would not line up.
  it('makes 32 groups of one byte each, in order', () => {
    const g = groupFingerprint(HEX);
    expect(g).toHaveLength(32);
    expect(g.slice(0, 4).map((x) => x.text)).toEqual(['26', 'EC', '54', 'A3']);
    expect(g.slice(-4).map((x) => x.text)).toEqual(['DE', 'AD', 'BE', 'EF']);
  });

  // The first and last 4 bytes are what the user is asked to check; highlighting the wrong ones weakens the MITM check.
  it('highlights exactly the first 4 and last 4 groups', () => {
    const flags = groupFingerprint(HEX).map((x) => x.highlight);
    expect(flags.filter(Boolean)).toHaveLength(8);
    expect(flags.slice(0, 4).every(Boolean)).toBe(true);
    expect(flags.slice(-4).every(Boolean)).toBe(true);
    expect(flags.slice(4, 28).some(Boolean)).toBe(false);
  });

  // A truncated or odd value must never be shown as if it were a valid fingerprint.
  it.each(['', 'AB', HEX.slice(1), HEX + '00', HEX.toLowerCase(), 'G' + HEX.slice(1), HEX.replace('26', '2 ')])(
    'rejects malformed input %j',
    (bad) => {
      expect(() => groupFingerprint(bad)).toThrow();
    },
  );
});
