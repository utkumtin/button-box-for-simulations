import { describe, expect, it } from 'vitest';
import { buildQr, qrGeometryFromGrid } from './qr';

describe('qr geometry', () => {
  // The scanner needs a 4-module light border; a wrong size or offset makes the code unreadable.
  it('adds a 4-module quiet zone on every side and offsets dark modules by it', () => {
    const g = qrGeometryFromGrid([
      [true, false],
      [false, true],
    ]);
    expect(g.size).toBe(2 + 8);
    expect(g.path).toBe('M4,4h1v1h-1zM5,5h1v1h-1z');
  });

  // Guards the real encoder integration: QR version 1 is 21 modules, so size must be 29 or more.
  it('encodes real text with size = modules + 8 and a non-empty path', () => {
    const g = buildQr('http://bbs-host.local');
    expect((g.size - 8 - 21) % 4).toBe(0);
    expect(g.size).toBeGreaterThanOrEqual(29);
    expect(g.path.length).toBeGreaterThan(0);
    // finder pattern top-left corner module is always dark
    expect(g.path.startsWith('M4,4h1v1h-1z')).toBe(true);
  });
});
