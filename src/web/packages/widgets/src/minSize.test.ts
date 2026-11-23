import { describe, expect, it } from 'vitest';
import type { Grid, ScreenInfo } from '@bbs/protocol';
import { fits, minimumSpan, type WidgetType } from './minSize';
import fixture from './minSize.cases.json';

interface Case {
  name: string;
  screen: ScreenInfo;
  grid: Grid;
  type: WidgetType;
  colSpan: number;
  rowSpan: number;
  fits: boolean;
  minimumSpan: { colSpan: number; rowSpan: number };
}

// Why: the editor stops a widget from shrinking below a fingertip, and the host refuses such a profile on save. Both read
// these cases (Core.Tests WidgetSizeTests too), so the two sides can never disagree about what fits on the phone.
describe('minimum widget size (shared cases)', () => {
  const cases = fixture.cases as Case[];

  it('has cases', () => {
    expect(cases.length).toBeGreaterThan(10);
  });

  for (const c of cases) {
    it(c.name, () => {
      expect(fits(c.type, { colSpan: c.colSpan, rowSpan: c.rowSpan }, c.grid, c.screen)).toBe(c.fits);
      expect(minimumSpan(c.type, c.grid, c.screen)).toEqual(c.minimumSpan);
    });
  }
});
