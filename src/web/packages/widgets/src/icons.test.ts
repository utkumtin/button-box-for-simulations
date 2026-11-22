import { describe, expect, it } from 'vitest';
import { ICON_KEYS, iconPath } from './icons';

describe('icon registry', () => {
  it('contains exactly the approved keys, so nothing unreviewed (brand icons) ships', () => {
    expect([...ICON_KEYS].sort()).toEqual(
      ['power', 'car-light-dimmed', 'car-light-high', 'alarm-light', 'hazard-lights', 'car-brake-parking', 'wiper', 'engine', 'minus', 'plus'].sort(),
    );
  });

  it('maps every key to a non-empty SVG path', () => {
    for (const key of ICON_KEYS) expect(iconPath(key)).toMatch(/^M[\s\S]+/);
  });

  it('returns undefined for unknown keys, including prototype names from untrusted layouts', () => {
    expect(iconPath('nope')).toBeUndefined();
    expect(iconPath('constructor')).toBeUndefined();
    expect(iconPath('__proto__')).toBeUndefined();
  });
});
