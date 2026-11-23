import type { Layout, ToggleWidget, Widget } from '@bbs/protocol';
import { describe, expect, it } from 'vitest';
import { layoutProblems, profileProblems } from './problems';
import { CATALOG, IPHONE, SMALL, emptyLayout, profileWith } from './testFixtures';

const toggle = (id: string, col: number, row: number, colSpan = 1, rowSpan = 1): ToggleWidget => ({
  id,
  type: 'toggle',
  action: 'beacon',
  cell: { col, row, colSpan, rowSpan },
});

const layoutWith = (widgets: Widget[], columns = 8, rows = 3): Layout => ({ ...emptyLayout(columns, rows), widgets });
const messages = (layout: Layout, assets?: ReadonlySet<string>) =>
  layoutProblems(layout, IPHONE, CATALOG, assets).map((p) => p.message);

describe('layoutProblems', () => {
  it('is empty for a good layout', () => {
    const sw: Widget = {
      id: 'lights',
      type: 'switch',
      cell: { col: 1, row: 0, colSpan: 1, rowSpan: 1 },
      positions: [
        { angle: -30, action: 'light-off' },
        { angle: 30, action: 'light-on' },
      ],
    };
    const dual: Widget = {
      id: 'engine',
      type: 'dualAction',
      cell: { col: 2, row: 0, colSpan: 1, rowSpan: 1 },
      tap: 'ignition-on',
      holdMode: 'fire',
    };
    expect(layoutProblems(layoutWith([toggle('a', 0, 0), sw, dual]), IPHONE, CATALOG)).toEqual([]);
  });

  it('flags a widget below the minimum size', () => {
    const p = layoutProblems(layoutWith([toggle('tiny', 0, 0)], 24, 12), IPHONE, CATALOG);
    expect(p).toHaveLength(1);
    expect(p[0]?.widgetId).toBe('tiny');
    expect(p[0]?.message).toMatch(/too small/);
  });

  it('flags a widget outside the grid', () => {
    const p = layoutProblems(layoutWith([toggle('out', 7, 0, 2, 1)]), IPHONE, CATALOG);
    expect(p.map((x) => x.message)).toEqual([expect.stringMatching(/outside the grid/)]);
  });

  // Overlapping touch areas on the phone: the host refuses these too.
  it('flags overlapping widgets, once per pair', () => {
    const p = layoutProblems(layoutWith([toggle('a', 0, 0, 2, 1), toggle('b', 1, 0)]), IPHONE, CATALOG);
    expect(p).toHaveLength(1);
    expect(p[0]?.widgetId).toBe('b');
    expect(p[0]?.message).toMatch(/overlaps/);
  });

  it('does not flag widgets that only touch', () => {
    expect(layoutProblems(layoutWith([toggle('a', 0, 0), toggle('b', 1, 0)]), IPHONE, CATALOG)).toEqual([]);
  });

  it('flags duplicate widget ids', () => {
    expect(messages(layoutWith([toggle('a', 0, 0), toggle('a', 1, 0)]))).toEqual([
      expect.stringMatching(/same id/),
    ]);
  });

  it('flags an action id that is not in the catalog', () => {
    const w: Widget = { ...toggle('a', 0, 0), action: 'warp-drive' };
    expect(messages(layoutWith([w]))).toEqual([expect.stringMatching(/does not exist/)]);
  });

  it('flags an unknown action on encoders, dual-actions and switch positions too', () => {
    const encoder: Widget = { id: 'e', type: 'encoder', minus: 'beacon', plus: 'nope', cell: { col: 0, row: 0, colSpan: 1, rowSpan: 1 } };
    const dual: Widget = { id: 'd', type: 'dualAction', hold: 'nope', holdMode: 'fire', cell: { col: 1, row: 0, colSpan: 1, rowSpan: 1 } };
    const sw: Widget = {
      id: 's',
      type: 'switch',
      cell: { col: 2, row: 0, colSpan: 1, rowSpan: 1 },
      positions: [
        { angle: 0, action: 'nope' },
        { angle: 10, action: 'beacon' },
      ],
    };
    expect(layoutProblems(layoutWith([encoder, dual, sw]), IPHONE, CATALOG).map((p) => p.widgetId)).toEqual(['e', 'd', 's']);
  });

  it('flags a toggle or encoder without an action', () => {
    const encoder: Widget = { id: 'e', type: 'encoder', minus: '', plus: 'beacon', cell: { col: 1, row: 0, colSpan: 1, rowSpan: 1 } };
    expect(messages(layoutWith([{ ...toggle('a', 0, 0), action: '' }, encoder]))).toEqual([
      expect.stringMatching(/no action/),
      expect.stringMatching(/no minus action/),
    ]);
  });

  it('flags a switch position without an action', () => {
    const sw: Widget = {
      id: 's',
      type: 'switch',
      cell: { col: 0, row: 0, colSpan: 1, rowSpan: 1 },
      positions: [{ angle: 0, action: 'beacon' }, { angle: 10 }],
    };
    expect(messages(layoutWith([sw]))).toEqual([expect.stringMatching(/position 2 has no action/)]);
  });

  it('flags a dual-action with neither tap nor hold', () => {
    const w: Widget = { id: 'd', type: 'dualAction', holdMode: 'fire', cell: { col: 0, row: 0, colSpan: 1, rowSpan: 1 } };
    expect(messages(layoutWith([w]))).toEqual([expect.stringMatching(/tap action, a hold action or both/)]);
  });

  it('flags cycle (press-count switching is not offered yet)', () => {
    const sw: Widget = {
      id: 's',
      type: 'switch',
      cycle: 'beacon',
      cell: { col: 0, row: 0, colSpan: 1, rowSpan: 1 },
      positions: [{ angle: 0, action: 'beacon' }, { angle: 10, action: 'beacon' }],
    };
    expect(messages(layoutWith([sw]))).toEqual([expect.stringMatching(/press-count/)]);
  });

  it('flags holdMode starterTakeover', () => {
    const w: Widget = { id: 'd', type: 'dualAction', tap: 'beacon', holdMode: 'starterTakeover', cell: { col: 0, row: 0, colSpan: 1, rowSpan: 1 } };
    expect(messages(layoutWith([w]))).toEqual([expect.stringMatching(/starter takeover/)]);
  });

  describe('asset ids', () => {
    const KNOWN = 'a'.repeat(64);
    const MISSING = 'b'.repeat(64);
    const withLook = (image: string, on?: string): ToggleWidget => ({
      ...toggle('a', 0, 0),
      look: { mode: 'images', image, ...(on ? { on } : {}) },
    });
    const rotary = (asset: string): Widget => ({
      id: 's',
      type: 'switch',
      cell: { col: 0, row: 0, colSpan: 1, rowSpan: 1 },
      positions: [{ angle: 0, action: 'beacon' }, { angle: 10, action: 'beacon' }],
      rotary: { knob: { asset } },
    });

    it('flags a look image, an on image and a rotary layer that are not in the library', () => {
      const assets = new Set([KNOWN]);
      expect(messages(layoutWith([withLook(MISSING)]), assets)).toEqual([expect.stringMatching(/image/)]);
      expect(messages(layoutWith([withLook(KNOWN, MISSING)]), assets)).toEqual([expect.stringMatching(/image/)]);
      expect(messages(layoutWith([rotary(MISSING)]), assets)).toEqual([expect.stringMatching(/image/)]);
    });

    it('accepts known assets and skips the check when no asset set is given', () => {
      expect(messages(layoutWith([withLook(KNOWN, KNOWN)]), new Set([KNOWN]))).toEqual([]);
      expect(messages(layoutWith([rotary(MISSING)]))).toEqual([]);
    });
  });

  it('flags a grid outside the limits without crashing', () => {
    const p = layoutProblems({ ...emptyLayout(0, 99), widgets: [toggle('a', 0, 0)] }, IPHONE, CATALOG);
    expect(p.map((x) => x.message)).toEqual([expect.stringMatching(/grid must have/)]);
  });

  it('flags a label that is too long or has control characters', () => {
    expect(messages(layoutWith([{ ...toggle('a', 0, 0), label: 'x'.repeat(33) }]))).toHaveLength(1);
    expect(messages(layoutWith([{ ...toggle('a', 0, 0), label: 'a\nb' }]))).toHaveLength(1);
    // Code points, like the schema and the host: 32 emoji are fine, 33 are not.
    expect(messages(layoutWith([{ ...toggle('a', 0, 0), label: '\u{1F69A}'.repeat(32) }]))).toHaveLength(0);
    expect(messages(layoutWith([{ ...toggle('a', 0, 0), label: '\u{1F69A}'.repeat(33) }]))).toHaveLength(1);
  });
});

describe('profileProblems', () => {
  it('names the template in each message and skips nothing', () => {
    const profile = profileWith({ ...emptyLayout(24, 12), widgets: [toggle('tiny', 0, 0)] });
    profile.layouts.push({ ...emptyLayout(8, 3, SMALL.id), widgets: [{ ...toggle('a', 0, 0), action: 'nope' }] });
    const p = profileProblems(profile, [IPHONE, SMALL], CATALOG);
    expect(p).toHaveLength(2);
    expect(p[0]?.message.startsWith('iPhone 14 Pro: ')).toBe(true);
    expect(p[1]?.message.startsWith('Small phone: ')).toBe(true);
    expect(p[1]?.templateId).toBe(SMALL.id);
  });

  it('flags a layout whose template is unknown', () => {
    const p = profileProblems(profileWith(emptyLayout(8, 3, 'mystery')), [IPHONE], CATALOG);
    expect(p).toHaveLength(1);
    expect(p[0]?.message).toMatch(/not known/);
  });

  it('is empty for a clean profile, which is what allows saving', () => {
    expect(profileProblems(profileWith({ ...emptyLayout(), widgets: [toggle('a', 0, 0)] }), [IPHONE], CATALOG)).toEqual([]);
  });
});
