import { describe, expect, it } from 'vitest';
import { IPHONE, SMALL } from '../lib/testFixtures';
import {
  cellBox,
  cutoutBox,
  frameScale,
  moveTarget,
  resizeTarget,
  safeAreaBox,
  sameCell,
  snapDelta,
} from './canvasGeometry';

// iPhone 14 Pro usable area: 852 - 59 - 59 = 734 wide, 393 - 20 = 373 high.
const AREA = { width: 734, height: 373 };
const G8x3 = { columns: 8, rows: 3 };
const G24x12 = { columns: 24, rows: 12 };

describe('frameScale', () => {
  it('keeps the aspect ratio of the screen', () => {
    const f = frameScale(IPHONE.screen, 426);
    expect(f.scale).toBeCloseTo(0.5);
    expect(f.width / f.height).toBeCloseTo(852 / 393);
  });

  it('never enlarges beyond the maximum scale', () => {
    expect(frameScale(IPHONE.screen, 5000).scale).toBe(1.5);
  });

  it('gives an empty frame before the width is known', () => {
    expect(frameScale(IPHONE.screen, 0)).toEqual({ scale: 0, width: 0, height: 0 });
  });
});

describe('safe area and cutout', () => {
  it('draws the safe area inside the insets, scaled', () => {
    const box = safeAreaBox(IPHONE.screen, 0.5);
    expect(box).toEqual({ x: 29.5, y: 0, width: 367, height: 186.5 });
  });

  it('puts the island on the left edge, centred vertically', () => {
    const box = cutoutBox('dynamicIsland', IPHONE.screen, 1);
    expect(box?.x).toBe(11);
    expect(box && box.y + box.height / 2).toBeCloseTo(393 / 2);
  });

  it('draws nothing for no cutout or an unknown one', () => {
    expect(cutoutBox('none', SMALL.screen, 1)).toBeNull();
    expect(cutoutBox('unknown', SMALL.screen, 1)).toBeNull();
  });
});

describe('snapDelta (the nearest cell, not the cell the pointer started in)', () => {
  it('rounds at the cell centre line', () => {
    expect(snapDelta(49, 100)).toBe(0);
    expect(snapDelta(50, 100)).toBe(1);
    expect(snapDelta(-49, 100)).toBe(0);
    expect(snapDelta(-51, 100)).toBe(-1);
  });

  it('never returns negative zero', () => {
    expect(Object.is(snapDelta(-10, 100), 0)).toBe(true);
  });
});

describe('moveTarget on the iPhone 14 Pro', () => {
  const cell = { col: 2, row: 1, colSpan: 1, rowSpan: 1 };

  it('8 x 3: a cell is 91.75 x 124.33 px; just under half a cell stays, just over moves', () => {
    expect(moveTarget(cell, 45.8, 0, G8x3, AREA)).toEqual(cell);
    expect(moveTarget(cell, 45.9, 0, G8x3, AREA)).toEqual({ ...cell, col: 3 });
    expect(moveTarget(cell, 0, 62.2, G8x3, AREA)).toEqual({ ...cell, row: 2 });
  });

  it('24 x 12: cells are small, the same drag is several cells', () => {
    // 30.58 px per column: 100 px is 3.27 cells -> 3.
    expect(moveTarget(cell, 100, 0, G24x12, AREA)).toEqual({ ...cell, col: 5 });
  });

  it('is clamped inside the grid, keeping the widget size', () => {
    const wide = { col: 5, row: 0, colSpan: 2, rowSpan: 1 };
    expect(moveTarget(wide, 10_000, -10_000, G8x3, AREA)).toEqual({ col: 6, row: 0, colSpan: 2, rowSpan: 1 });
    expect(moveTarget(wide, -10_000, 10_000, G8x3, AREA)).toEqual({ col: 0, row: 2, colSpan: 2, rowSpan: 1 });
  });
});

describe('resizeTarget', () => {
  const cell = { col: 3, row: 1, colSpan: 2, rowSpan: 1 };

  it('the bottom-right handle changes the span only', () => {
    expect(resizeTarget(cell, 'se', 92, 0, G8x3, AREA)).toEqual({ col: 3, row: 1, colSpan: 3, rowSpan: 1 });
  });

  it('the top-left handle moves the origin and keeps the opposite corner', () => {
    const r = resizeTarget(cell, 'nw', -92, -124, G8x3, AREA);
    expect(r).toEqual({ col: 2, row: 0, colSpan: 3, rowSpan: 2 });
    expect(r.col + r.colSpan).toBe(5);
    expect(r.row + r.rowSpan).toBe(2);
  });

  it('an edge handle changes one dimension only', () => {
    expect(resizeTarget(cell, 'e', 92, 500, G8x3, AREA)).toEqual({ col: 3, row: 1, colSpan: 3, rowSpan: 1 });
  });

  it('cannot shrink below one cell or grow outside the grid', () => {
    expect(resizeTarget(cell, 'e', -10_000, 0, G8x3, AREA).colSpan).toBe(1);
    expect(resizeTarget(cell, 'w', 10_000, 0, G8x3, AREA)).toEqual({ col: 4, row: 1, colSpan: 1, rowSpan: 1 });
    expect(resizeTarget(cell, 'se', 10_000, 10_000, G8x3, AREA)).toEqual({ col: 3, row: 1, colSpan: 5, rowSpan: 2 });
  });
});

describe('cellBox and sameCell', () => {
  it('maps a cell to pixels', () => {
    expect(cellBox({ col: 1, row: 1, colSpan: 2, rowSpan: 1 }, { columns: 2, rows: 2 }, { width: 200, height: 100 })).toEqual({
      x: 100,
      y: 50,
      width: 200,
      height: 50,
    });
  });

  it('compares all four numbers', () => {
    const a = { col: 0, row: 0, colSpan: 1, rowSpan: 1 };
    expect(sameCell(a, { ...a })).toBe(true);
    expect(sameCell(a, { ...a, rowSpan: 2 })).toBe(false);
  });
});
