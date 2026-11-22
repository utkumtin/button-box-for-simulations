import { describe, expect, it } from 'vitest';
import { validateDeviceName } from './deviceName';

describe('validateDeviceName', () => {
  // The host rejects these with 400; catching them first gives the user a clear message instead of a failed request.
  it('rejects empty names and control characters', () => {
    expect(validateDeviceName('')).not.toBeNull();
    expect(validateDeviceName('a\nb')).not.toBeNull();
    expect(validateDeviceName('a\u0000b')).not.toBeNull();
    expect(validateDeviceName('a\u007Fb')).not.toBeNull();
    expect(validateDeviceName('a\u001Fb')).not.toBeNull();
  });

  // The schema limit counts characters; an emoji is one character, not two UTF-16 units.
  it('counts code points, not UTF-16 units, for the 64 limit', () => {
    expect(validateDeviceName('a'.repeat(64))).toBeNull();
    expect(validateDeviceName('a'.repeat(65))).not.toBeNull();
    expect(validateDeviceName('😀'.repeat(64))).toBeNull();
    expect(validateDeviceName('😀'.repeat(65))).not.toBeNull();
  });

  it('accepts ordinary names, including spaces and non-ASCII', () => {
    expect(validateDeviceName("Utku's iPhone 15")).toBeNull();
    expect(validateDeviceName('Telefon Ömer')).toBeNull();
  });
});
