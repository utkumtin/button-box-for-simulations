import type { Widget } from '@bbs/protocol';
import type { GestureOut } from '@bbs/widgets';
import { describe, expect, it } from 'vitest';
import { testAction, testHint } from './testAction';

const cell = { col: 0, row: 0, colSpan: 1, rowSpan: 1 };
const gesture = (widgetId: string, g: 'tap' | 'hold' | 'minus' | 'plus' | 'previous' | 'next'): GestureOut => ({
  type: 'gesture',
  widgetId,
  gesture: g,
});
const select = (widgetId: string, position: number): GestureOut => ({ type: 'select', widgetId, position });

const toggle: Widget = { id: 't', type: 'toggle', action: 'beacon', cell };
const encoder: Widget = { id: 'e', type: 'encoder', minus: 'down', plus: 'up', cell };
const dual: Widget = { id: 'd', type: 'dualAction', tap: 'on', hold: 'start', holdMode: 'fire', cell };
const sw: Widget = {
  id: 's',
  type: 'switch',
  cell,
  positions: [
    { angle: -30, action: 'p0' },
    { angle: 0, action: 'p1' },
    { angle: 30, action: 'p2' },
  ],
};

describe('testAction', () => {
  it('toggle: tap sends the action, other gestures nothing', () => {
    expect(testAction(toggle, gesture('t', 'tap'), {}).actionId).toBe('beacon');
    expect(testAction(toggle, gesture('t', 'hold'), {}).actionId).toBeNull();
  });

  it('encoder: minus and plus', () => {
    expect(testAction(encoder, gesture('e', 'minus'), {}).actionId).toBe('down');
    expect(testAction(encoder, gesture('e', 'plus'), {}).actionId).toBe('up');
    expect(testAction(encoder, gesture('e', 'tap'), {}).actionId).toBeNull();
  });

  it('dual-action: tap and hold (fire)', () => {
    expect(testAction(dual, gesture('d', 'tap'), {}).actionId).toBe('on');
    expect(testAction(dual, gesture('d', 'hold'), {}).actionId).toBe('start');
  });

  it('dual-action: a missing tap or hold sends nothing; starter takeover is not played in test mode', () => {
    expect(testAction({ ...dual, tap: undefined } as Widget, gesture('d', 'tap'), {}).actionId).toBeNull();
    expect(testAction({ ...dual, hold: undefined } as Widget, gesture('d', 'hold'), {}).actionId).toBeNull();
    expect(testAction({ ...dual, holdMode: 'starterTakeover' }, gesture('d', 'hold'), {}).actionId).toBeNull();
  });

  it('switch select n sends position n and remembers it', () => {
    const r = testAction(sw, select('s', 2), {});
    expect(r.actionId).toBe('p2');
    expect(r.positions).toEqual({ s: 2 });
    expect(testAction(sw, select('s', 7), {}).actionId).toBeNull();
    expect(testAction(sw, select('s', -1), {}).actionId).toBeNull();
    expect(testAction(sw, select('s', 1.5), {}).actionId).toBeNull();
  });

  // The phone clamps at the ends and the host sends nothing there; the test button must behave the same.
  it('switch previous at position 0 and next at the end send nothing and stay put', () => {
    expect(testAction(sw, gesture('s', 'previous'), {})).toEqual({ actionId: null, positions: {} });
    const atEnd = { s: 2 };
    const r = testAction(sw, gesture('s', 'next'), atEnd);
    expect(r.actionId).toBeNull();
    expect(r.positions).toBe(atEnd);
  });

  it('switch next/previous walk through the positions from 0', () => {
    const a = testAction(sw, gesture('s', 'next'), {});
    expect(a).toEqual({ actionId: 'p1', positions: { s: 1 } });
    const b = testAction(sw, gesture('s', 'next'), a.positions);
    expect(b).toEqual({ actionId: 'p2', positions: { s: 2 } });
    const c = testAction(sw, gesture('s', 'previous'), b.positions);
    expect(c).toEqual({ actionId: 'p1', positions: { s: 1 } });
  });

  it('keeps positions of different switches apart', () => {
    const other: Widget = { ...sw, id: 's2' };
    const a = testAction(sw, gesture('s', 'next'), {});
    expect(testAction(other, gesture('s2', 'next'), a.positions).positions).toEqual({ s: 1, s2: 1 });
  });

  it('does not mutate the positions it is given', () => {
    const given = Object.freeze({ s: 0 });
    expect(() => testAction(sw, gesture('s', 'next'), given)).not.toThrow();
    expect(given).toEqual({ s: 0 });
  });

  it('ignores a gesture meant for another widget or a select on a non-switch', () => {
    expect(testAction(toggle, gesture('other', 'tap'), {}).actionId).toBeNull();
    expect(testAction(toggle, select('t', 0), {}).actionId).toBeNull();
  });

  it('sends nothing for an empty action', () => {
    expect(testAction({ ...toggle, action: '' }, gesture('t', 'tap'), {}).actionId).toBeNull();
  });
});

describe('testHint', () => {
  // Decisions section 10: ETS2/ATS drop test presses while the game is not listening.
  it('is shown for ETS2 and ATS only', () => {
    expect(testHint('ets2')).toMatch(/running, focused and not paused/);
    expect(testHint('ats')).toBe(testHint('ets2'));
    expect(testHint('acc')).toBeNull();
  });
});
