import { describe, expect, it } from 'vitest';
import { angleOf, layerBox, nearestPosition } from './geometry';

describe('geometry', () => {
  it('measures angles from 12 o clock, clockwise', () => {
    expect(angleOf(0, -1)).toBeCloseTo(0);
    expect(angleOf(1, 0)).toBeCloseTo(90);
    expect(angleOf(0, 1)).toBeCloseTo(180);
    expect(angleOf(-1, 0)).toBeCloseTo(270);
  });

  it('wraps nearest-position distance around 360', () => {
    expect(nearestPosition([350, 90], 5)).toBe(0);
    expect(nearestPosition([-90, 0, 90], 280)).toBe(0);
  });

  it('applies layer scale and offsets as fractions of the square', () => {
    expect(layerBox(200, undefined)).toEqual({ left: 0, top: 0, size: 200 });
    expect(layerBox(200, { scale: 0.5, offsetX: 0.25, offsetY: -0.25 })).toEqual({ left: 100, top: 0, size: 100 });
  });
});
