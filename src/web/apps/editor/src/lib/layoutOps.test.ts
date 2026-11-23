import type { Layout, Profile, Widget } from '@bbs/protocol';
import { describe, expect, it } from 'vitest';
import {
  addLayout,
  addWidget,
  blankWidget,
  copyLayout,
  deleteWidget,
  duplicateWidget,
  moveWidget,
  removeLayout,
  renameProfile,
  resizeWidget,
  setGrid,
  updateWidget,
  type OpResult,
} from './layoutOps';
import { IPHONE, SMALL, emptyLayout, profileWith } from './testFixtures';

/** Unwraps a successful result; a refusal fails the test with its reason. */
function ok<E>(result: OpResult<E>): Extract<OpResult<E>, { ok: true }> {
  if (!result.ok) {
    throw new Error(`refused: ${result.reason}`);
  }
  return result;
}

function layoutOf(profile: Profile, templateId = IPHONE.id): Layout {
  const layout = profile.layouts.find((l) => l.templateId === templateId);
  if (!layout) {
    throw new Error('no layout');
  }
  return layout;
}

const toggle = (id: string, col: number, row: number, colSpan = 2, rowSpan = 2): Widget => ({
  id,
  type: 'toggle',
  action: 'beacon',
  cell: { col, row, colSpan, rowSpan },
});

describe('layouts', () => {
  it('adds an empty 8 x 3 layout and refuses a second one for the same template', () => {
    const empty: Profile = { ...profileWith(), layouts: [] };
    const added = ok(addLayout(empty, IPHONE)).profile;
    expect(layoutOf(added).grid).toEqual({ columns: 8, rows: 3 });
    expect(layoutOf(added).widgets).toEqual([]);
    expect(addLayout(added, IPHONE).ok).toBe(false);
  });

  it('removes a layout', () => {
    expect(ok(removeLayout(profileWith(), IPHONE.id)).profile.layouts).toHaveLength(0);
    expect(removeLayout(profileWith(), 'nope').ok).toBe(false);
  });

  it('never mutates its input', () => {
    const p = profileWith(emptyLayout(24, 12));
    const before = structuredClone(p);
    const added = ok(addWidget(p, IPHONE, blankWidget('toggle'))).profile;
    ok(moveWidget(added, IPHONE, 'toggle-1', { col: 5, row: 5 }));
    ok(setGrid(p, IPHONE, { columns: 12, rows: 6 }));
    expect(p).toEqual(before);
  });
});

describe('adding widgets', () => {
  // Decisions §6 example: on 24 x 12 a toggle needs 2 x 2 cells and an encoder 3 x 2.
  it('places a widget at its minimum span on a fine grid', () => {
    const p = profileWith(emptyLayout(24, 12));
    const withToggle = ok(addWidget(p, IPHONE, blankWidget('toggle')));
    expect(withToggle.widgetId).toBe('toggle-1');
    expect(layoutOf(withToggle.profile).widgets[0]?.cell).toEqual({ col: 0, row: 0, colSpan: 2, rowSpan: 2 });
    const withEncoder = ok(addWidget(withToggle.profile, IPHONE, blankWidget('encoder')));
    const encoder = layoutOf(withEncoder.profile).widgets[1];
    expect(encoder?.cell).toEqual({ col: 2, row: 0, colSpan: 3, rowSpan: 2 });
  });

  it('uses one cell where one cell is big enough', () => {
    const p = ok(addWidget(profileWith(), IPHONE, blankWidget('switch'))).profile;
    expect(layoutOf(p).widgets[0]?.cell).toEqual({ col: 0, row: 0, colSpan: 1, rowSpan: 1 });
  });

  it('honours a larger requested span but never a smaller one', () => {
    const p = profileWith(emptyLayout(24, 12));
    const big = ok(addWidget(p, IPHONE, blankWidget('toggle'), { colSpan: 4, rowSpan: 3 })).profile;
    expect(layoutOf(big).widgets[0]?.cell).toMatchObject({ colSpan: 4, rowSpan: 3 });
    const small = ok(addWidget(p, IPHONE, blankWidget('toggle'), { colSpan: 1, rowSpan: 1 })).profile;
    expect(layoutOf(small).widgets[0]?.cell).toMatchObject({ colSpan: 2, rowSpan: 2 });
  });

  it('skips occupied cells and refuses when the grid is full', () => {
    let p = profileWith(emptyLayout(2, 1));
    p = ok(addWidget(p, IPHONE, blankWidget('toggle'))).profile;
    p = ok(addWidget(p, IPHONE, blankWidget('toggle'))).profile;
    expect(layoutOf(p).widgets.map((w) => w.cell.col)).toEqual([0, 1]);
    const full = addWidget(p, IPHONE, blankWidget('toggle'));
    expect(full.ok).toBe(false);
  });

  it('removes an empty label instead of writing ""', () => {
    const p = ok(addWidget(profileWith(), IPHONE, { ...blankWidget('toggle'), label: '' })).profile;
    expect('label' in (layoutOf(p).widgets[0] as object)).toBe(false);
  });
});

describe('resizing and moving', () => {
  const base = () => {
    const p = ok(addWidget(profileWith(emptyLayout(24, 12)), IPHONE, blankWidget('toggle'))).profile;
    return ok(addWidget(p, IPHONE, blankWidget('encoder'))).profile; // toggle-1 at 0,0 2x2; encoder-1 at 2,0 3x2
  };

  // The whole point of the rule: a toggle on a 31 pt grid must not shrink to 1 x 1 (31 pt < 44 pt).
  it('refuses a toggle below 2 x 2 and accepts 2 x 2', () => {
    expect(resizeWidget(base(), IPHONE, 'toggle-1', { col: 0, row: 0, colSpan: 1, rowSpan: 1 }).ok).toBe(false);
    expect(resizeWidget(base(), IPHONE, 'toggle-1', { col: 0, row: 0, colSpan: 2, rowSpan: 2 }).ok).toBe(true);
  });

  it('refuses an encoder 2 x 2 (61 pt wide) and accepts 3 x 2', () => {
    const cell = { col: 2, row: 0 };
    expect(resizeWidget(base(), IPHONE, 'encoder-1', { ...cell, colSpan: 2, rowSpan: 2 }).ok).toBe(false);
    expect(resizeWidget(base(), IPHONE, 'encoder-1', { ...cell, colSpan: 3, rowSpan: 2 }).ok).toBe(true);
  });

  it('refuses a resize outside the grid', () => {
    expect(resizeWidget(base(), IPHONE, 'toggle-1', { col: 23, row: 0, colSpan: 2, rowSpan: 2 }).ok).toBe(false);
    expect(resizeWidget(base(), IPHONE, 'toggle-1', { col: -1, row: 0, colSpan: 2, rowSpan: 2 }).ok).toBe(false);
    expect(resizeWidget(base(), IPHONE, 'toggle-1', { col: 0, row: 0, colSpan: 2.5, rowSpan: 2 }).ok).toBe(false);
  });

  it('refuses a resize that overlaps and allows growing into free space', () => {
    expect(resizeWidget(base(), IPHONE, 'toggle-1', { col: 0, row: 0, colSpan: 3, rowSpan: 2 }).ok).toBe(false);
    const grown = ok(resizeWidget(base(), IPHONE, 'toggle-1', { col: 0, row: 0, colSpan: 2, rowSpan: 5 }));
    expect(layoutOf(grown.profile).widgets[0]?.cell.rowSpan).toBe(5);
  });

  // Overlapping touch areas on the phone would make the touch ambiguous.
  it('refuses to move a widget onto another one', () => {
    const r = moveWidget(base(), IPHONE, 'toggle-1', { col: 3, row: 0 });
    expect(r.ok).toBe(false);
    expect(ok(moveWidget(base(), IPHONE, 'toggle-1', { col: 5, row: 0 })).profile).not.toEqual(base());
  });

  it('refuses to move a widget out of the grid', () => {
    expect(moveWidget(base(), IPHONE, 'toggle-1', { col: 23, row: 0 }).ok).toBe(false);
    expect(moveWidget(base(), IPHONE, 'toggle-1', { col: 0, row: 11 }).ok).toBe(false);
  });

  it('refuses an unknown widget', () => {
    expect(moveWidget(base(), IPHONE, 'ghost', { col: 0, row: 0 }).ok).toBe(false);
  });
});

describe('grid', () => {
  it('accepts 8 x 3 to 24 x 16 for an empty layout and refuses values outside 1-24 x 1-16', () => {
    const p = profileWith(emptyLayout(8, 3));
    expect(setGrid(p, IPHONE, { columns: 24, rows: 16 }).ok).toBe(true);
    expect(setGrid(p, IPHONE, { columns: 1, rows: 1 }).ok).toBe(true);
    for (const grid of [
      { columns: 25, rows: 3 },
      { columns: 8, rows: 17 },
      { columns: 0, rows: 3 },
      { columns: 8, rows: 0 },
      { columns: 8.5, rows: 3 },
    ]) {
      expect(setGrid(p, IPHONE, grid).ok).toBe(false);
    }
  });

  it('accepts a finer grid when a spanning widget still fits', () => {
    const layout = { ...emptyLayout(8, 3), widgets: [toggle('a', 0, 0, 3, 3)] };
    expect(setGrid(profileWith(layout), IPHONE, { columns: 24, rows: 16 }).ok).toBe(true);
  });

  it('refuses a coarser grid when a widget would fall outside', () => {
    const layout = { ...emptyLayout(24, 12), widgets: [toggle('a', 20, 0)] };
    const r = setGrid(profileWith(layout), IPHONE, { columns: 8, rows: 3 });
    expect(r.ok).toBe(false);
  });

  it('refuses a finer grid when a widget would drop below the minimum', () => {
    const layout = { ...emptyLayout(8, 3), widgets: [toggle('a', 0, 0, 1, 1)] };
    expect(setGrid(profileWith(layout), IPHONE, { columns: 24, rows: 12 }).ok).toBe(false);
  });
});

describe('copy layout to another template', () => {
  // On 24 x 16 the iPhone cells are 30.6 x 23.3 pt; on the small phone 23.7 x 20 pt.
  const source: Layout = {
    ...emptyLayout(24, 16),
    widgets: [
      toggle('ok-toggle', 0, 0, 2, 3), // small: 47 x 60 pt, fits
      toggle('tiny-toggle', 4, 0, 2, 2), // small: 47 x 40 pt, below 44
      { id: 'tiny-encoder', type: 'encoder', minus: 'a', plus: 'b', cell: { col: 8, row: 0, colSpan: 3, rowSpan: 2 } }, // small: 71 x 40
    ],
  };

  it('keeps grid and cells and flags the widgets that are too small', () => {
    const r = ok(copyLayout(profileWith(source), IPHONE.id, SMALL));
    expect(r.belowMinimum).toEqual(['tiny-toggle', 'tiny-encoder']);
    const copy = layoutOf(r.profile, SMALL.id);
    expect(copy.grid).toEqual(source.grid);
    expect(copy.widgets.map((w) => w.cell)).toEqual(source.widgets.map((w) => w.cell));
    expect(layoutOf(r.profile, IPHONE.id)).toEqual(source);
  });

  it('copies deeply: editing the copy leaves the source alone', () => {
    const r = ok(copyLayout(profileWith(source), IPHONE.id, SMALL));
    const edited = ok(deleteWidget(r.profile, SMALL.id, 'ok-toggle')).profile;
    expect(layoutOf(edited, IPHONE.id).widgets).toHaveLength(3);
  });

  it('flags nothing when everything fits', () => {
    const r = ok(copyLayout(profileWith(emptyLayout(8, 3)), IPHONE.id, SMALL));
    expect(r.belowMinimum).toEqual([]);
  });

  it('refuses to overwrite an existing layout unless asked, and to copy onto itself', () => {
    const both = ok(copyLayout(profileWith(source), IPHONE.id, SMALL)).profile;
    expect(copyLayout(both, IPHONE.id, SMALL).ok).toBe(false);
    expect(copyLayout(both, IPHONE.id, SMALL, true).ok).toBe(true);
    expect(copyLayout(both, IPHONE.id, IPHONE).ok).toBe(false);
  });
});

describe('duplicate and delete', () => {
  it('duplicates into the next free spot with a new id', () => {
    const p = ok(addWidget(profileWith(emptyLayout(24, 12)), IPHONE, { ...blankWidget('toggle'), label: 'Beacon' })).profile;
    const r = ok(duplicateWidget(p, IPHONE, 'beacon'));
    expect(r.widgetId).toBe('beacon-2');
    const [a, b] = layoutOf(r.profile).widgets;
    expect(b?.cell).toEqual({ col: 2, row: 0, colSpan: 2, rowSpan: 2 });
    expect(b?.label).toBe('Beacon');
    expect(a?.id).toBe('beacon');
  });

  it('refuses to duplicate when there is no room', () => {
    const p = ok(addWidget(profileWith(emptyLayout(1, 1)), IPHONE, blankWidget('toggle'))).profile;
    expect(duplicateWidget(p, IPHONE, 'toggle-1').ok).toBe(false);
  });

  it('deletes a widget', () => {
    const p = ok(addWidget(profileWith(), IPHONE, blankWidget('toggle'))).profile;
    expect(layoutOf(ok(deleteWidget(p, IPHONE.id, 'toggle-1')).profile).widgets).toEqual([]);
    expect(deleteWidget(p, IPHONE.id, 'ghost').ok).toBe(false);
  });
});

describe('updating properties', () => {
  // The schema gives label minLength 1; the host would refuse a profile that contains "".
  it('removes a cleared widget label instead of writing ""', () => {
    let p = ok(addWidget(profileWith(), IPHONE, { ...blankWidget('toggle'), label: 'Beacon' })).profile;
    p = ok(updateWidget(p, IPHONE.id, 'beacon', (w) => ({ ...w, label: '' }))).profile;
    expect('label' in (layoutOf(p).widgets[0] as object)).toBe(false);
  });

  it('removes cleared position label, icon and action', () => {
    let p = ok(addWidget(profileWith(), IPHONE, blankWidget('switch'))).profile;
    p = ok(
      updateWidget(p, IPHONE.id, 'switch-1', (w) =>
        w.type === 'switch'
          ? { ...w, positions: [{ label: 'OFF', angle: -30, action: 'x', icon: 'car' }, w.positions[1]!] }
          : w,
      ),
    ).profile;
    p = ok(
      updateWidget(p, IPHONE.id, 'switch-1', (w) =>
        w.type === 'switch'
          ? { ...w, positions: [{ label: '', angle: -30, action: '', icon: '' }, w.positions[1]!] }
          : w,
      ),
    ).profile;
    const sw = layoutOf(p).widgets[0];
    const first = sw?.type === 'switch' ? sw.positions[0] : undefined;
    expect(first).toEqual({ angle: -30 });
    expect(Object.keys(first ?? {})).toEqual(['angle']);
  });

  it('removes empty state fields, empty state, empty look and empty rotary layers', () => {
    let p = ok(addWidget(profileWith(), IPHONE, blankWidget('toggle'))).profile;
    p = ok(
      updateWidget(p, IPHONE.id, 'toggle-1', (w) => ({
        ...w,
        state: { lit: '', led: '' },
        ...(w.type === 'toggle' ? { look: { mode: 'icon' as const, icon: '' } } : {}),
      })),
    ).profile;
    const t = layoutOf(p).widgets[0] as object;
    expect('state' in t).toBe(false);
    expect('look' in t).toBe(false);

    let s = ok(addWidget(profileWith(), IPHONE, blankWidget('switch'))).profile;
    s = ok(
      updateWidget(s, IPHONE.id, 'switch-1', (w) =>
        w.type === 'switch' ? { ...w, rotary: { base: { asset: '' } }, cycle: '' } : w,
      ),
    ).profile;
    const sw = layoutOf(s).widgets[0] as object;
    expect('rotary' in sw).toBe(false);
    expect('cycle' in sw).toBe(false);
  });

  it('keeps one state field when the other is cleared', () => {
    let p = ok(addWidget(profileWith(), IPHONE, blankWidget('toggle'))).profile;
    p = ok(updateWidget(p, IPHONE.id, 'toggle-1', (w) => ({ ...w, state: { lit: 'truck.light.beacon', led: '' } }))).profile;
    expect(layoutOf(p).widgets[0]?.state).toEqual({ lit: 'truck.light.beacon' });
  });

  // Moving and resizing have their own checks; an update must not smuggle a different cell, id or type in.
  it('keeps id, type and cell whatever the change returns', () => {
    let p = ok(addWidget(profileWith(), IPHONE, blankWidget('toggle'))).profile;
    p = ok(
      updateWidget(p, IPHONE.id, 'toggle-1', (w) => ({ ...w, id: 'other', cell: { col: 7, row: 2, colSpan: 1, rowSpan: 1 } })),
    ).profile;
    expect(layoutOf(p).widgets[0]).toMatchObject({ id: 'toggle-1', type: 'toggle', cell: { col: 0, row: 0 } });
  });
});

describe('renaming the profile', () => {
  it('trims and sets the name', () => {
    expect(ok(renameProfile(profileWith(), '  My night  ')).profile.name).toBe('My night');
  });

  it('refuses an empty, blank, control-character or over-long name', () => {
    for (const name of ['', '   ', 'a\nb', 'a\u0000b', 'a\u007fb', 'x'.repeat(65)]) {
      expect(renameProfile(profileWith(), name).ok).toBe(false);
    }
    expect(renameProfile(profileWith(), 'x'.repeat(64)).ok).toBe(true);
  });

  // The schema and the host count code points: 64 emoji are a valid name although they are 128 UTF-16 units.
  it('counts the name length in code points', () => {
    expect(renameProfile(profileWith(), '\u{1F69A}'.repeat(64)).ok).toBe(true);
    expect(renameProfile(profileWith(), '\u{1F69A}'.repeat(65)).ok).toBe(false);
  });
});
