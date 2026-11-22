import { describe, expect, it } from 'vitest';
import type { Layout, Widget } from '@bbs/protocol';
import { TOUCH, type GestureOut } from '../contract';
import { TouchEngine, type Visual } from './engine';

// 4 x 2 grid on a 400 x 200 px surface: every cell is 100 x 100 px.
const toggle: Widget = { id: 'tog', type: 'toggle', cell: { col: 0, row: 0, colSpan: 1, rowSpan: 1 }, action: 'a' };
const guarded: Widget = { id: 'grd', type: 'toggle', cell: { col: 1, row: 0, colSpan: 1, rowSpan: 1 }, action: 'a', longPressGuard: true };
const encoder: Widget = { id: 'enc', type: 'encoder', cell: { col: 2, row: 0, colSpan: 2, rowSpan: 1 }, minus: 'm', plus: 'p' };
const sw: Widget = {
  id: 'sw',
  type: 'switch',
  cell: { col: 0, row: 1, colSpan: 2, rowSpan: 1 },
  positions: [{ angle: -90 }, { angle: 0 }, { angle: 90 }],
};
const guardedSw: Widget = { ...sw, id: 'gsw', cell: { col: 2, row: 1, colSpan: 1, rowSpan: 1 }, longPressGuard: true };
const dual: Widget = { id: 'dual', type: 'dualAction', cell: { col: 3, row: 1, colSpan: 1, rowSpan: 1 }, tap: 't', hold: 'h', holdMode: 'fire' };

function layout(widgets: Widget[]): Layout {
  return { schemaVersion: 1, templateId: 't', grid: { columns: 4, rows: 2 }, widgets };
}

function setup(widgets: Widget[] = [toggle, guarded, encoder, sw, guardedSw, dual], shown: Record<string, number> = {}) {
  let now = 0;
  let nextId = 1;
  const timers = new Map<number, { at: number; fn: () => void }>();
  const out: GestureOut[] = [];
  const visuals: (Visual | null)[] = [];
  const engine = new TouchEngine({
    timers: {
      setTimer: (ms, fn) => {
        const id = nextId++;
        timers.set(id, { at: now + ms, fn });
        return id;
      },
      clearTimer: (h) => {
        timers.delete(h as number);
      },
    },
    onGesture: (g) => out.push(g),
    onVisual: (v) => visuals.push(v),
    shownPosition: (id) => shown[id] ?? 0,
  });
  engine.setLayout(layout(widgets));
  engine.setSize(400, 200);
  const advance = (ms: number) => {
    now += ms;
    for (const [id, t] of [...timers]) {
      if (t.at <= now) {
        timers.delete(id);
        t.fn();
      }
    }
  };
  return { engine, out, visuals, advance, shown, pending: () => timers.size };
}

const tapOf = (widgetId: string, gesture: string) => ({ type: 'gesture', widgetId, gesture });

describe('tap vs drag', () => {
  it('fires one tap on release without movement, however long the press (plain push button)', () => {
    const { engine, out, advance } = setup();
    engine.pointerDown(1, 50, 50);
    advance(3000);
    engine.pointerUp(1);
    expect(out).toEqual([tapOf('tog', 'tap')]);
  });

  it('keeps a tap when the finger jitters within the 10 px threshold', () => {
    const { engine, out } = setup();
    engine.pointerDown(1, 50, 50);
    engine.pointerMove(1, 50 + TOUCH.dragThresholdPx, 50);
    engine.pointerUp(1);
    expect(out).toHaveLength(1);
  });

  it('cancels the tap once the finger moves beyond the threshold, even if it returns', () => {
    const { engine, out } = setup();
    engine.pointerDown(1, 50, 50);
    engine.pointerMove(1, 50 + TOUCH.dragThresholdPx + 1, 50);
    engine.pointerMove(1, 50, 50);
    engine.pointerUp(1);
    expect(out).toEqual([]);
  });

  it('fires nothing for a touch that lands outside every widget', () => {
    const { engine, out } = setup([toggle]);
    engine.pointerDown(1, 350, 150);
    engine.pointerUp(1);
    expect(out).toEqual([]);
  });
});

describe('single active touch', () => {
  it('ignores a second finger while the first owns the grid, even on another widget', () => {
    const { engine, out } = setup();
    expect(engine.pointerDown(1, 50, 50)).toBe(true);
    expect(engine.pointerDown(2, 150, 50)).toBe(false);
    engine.pointerUp(2);
    engine.pointerUp(1);
    expect(out).toEqual([tapOf('tog', 'tap')]);
  });

  it('only the first of two simultaneous downs owns the interaction', () => {
    const { engine, out } = setup();
    expect(engine.pointerDown(1, 50, 50)).toBe(true);
    expect(engine.pointerDown(2, 50, 50)).toBe(false);
    engine.pointerUp(1);
    expect(out).toHaveLength(1);
  });

  it('a second finger during a guarded hold never triggers anything and does not disturb the first', () => {
    const { engine, out, advance } = setup();
    engine.pointerDown(1, 150, 50);
    advance(200);
    engine.pointerDown(2, 50, 50);
    engine.pointerUp(2);
    advance(300);
    expect(out).toEqual([tapOf('grd', 'tap')]);
    engine.pointerUp(1);
    expect(out).toHaveLength(1);
  });

  it('moves of a non-owner pointer do not drag the owner', () => {
    const { engine, out } = setup();
    engine.pointerDown(1, 50, 50);
    engine.pointerMove(2, 90, 90);
    engine.pointerUp(1);
    expect(out).toHaveLength(1);
  });

  it('works again for the next touch after the owner lifts', () => {
    const { engine, out } = setup();
    engine.pointerDown(1, 50, 50);
    engine.pointerUp(1);
    expect(engine.pointerDown(2, 50, 50)).toBe(true);
    engine.pointerUp(2);
    expect(out).toHaveLength(2);
  });

  it('a finger landing outside all widgets still blocks others until it lifts', () => {
    const { engine, out } = setup([toggle]);
    expect(engine.pointerDown(1, 350, 150)).toBe(true);
    expect(engine.pointerDown(2, 50, 50)).toBe(false);
    engine.pointerUp(2);
    expect(out).toEqual([]);
    engine.pointerUp(1);
    expect(engine.pointerDown(3, 50, 50)).toBe(true);
  });
});

describe('long-press guard', () => {
  it('fires at 0.5 s while the finger is still down, not on release', () => {
    const { engine, out, advance } = setup();
    engine.pointerDown(1, 150, 50);
    advance(TOUCH.holdMs - 1);
    expect(out).toEqual([]);
    advance(1);
    expect(out).toEqual([tapOf('grd', 'tap')]);
    engine.pointerUp(1);
    expect(out).toHaveLength(1);
  });

  it('fires nothing when released before 0.5 s', () => {
    const { engine, out, advance } = setup();
    engine.pointerDown(1, 150, 50);
    advance(TOUCH.holdMs - 1);
    engine.pointerUp(1);
    advance(1000);
    expect(out).toEqual([]);
  });

  it('fires nothing when the finger moved beyond the threshold before 0.5 s', () => {
    const { engine, out, advance } = setup();
    engine.pointerDown(1, 150, 50);
    engine.pointerMove(1, 150, 80);
    advance(1000);
    engine.pointerUp(1);
    expect(out).toEqual([]);
  });

  it('guards encoder halves too: the half is the landing half, fired at 0.5 s', () => {
    const { engine, out, advance } = setup([{ ...encoder, longPressGuard: true }]);
    engine.pointerDown(1, 350, 50);
    engine.pointerUp(1);
    expect(out).toEqual([]);
    engine.pointerDown(2, 250, 50);
    advance(TOUCH.holdMs);
    expect(out).toEqual([tapOf('enc', 'minus')]);
  });

  it('guards switch half taps at 0.5 s', () => {
    const { engine, out, advance } = setup();
    engine.pointerDown(1, 220, 150);
    engine.pointerUp(1);
    expect(out).toEqual([]);
    engine.pointerDown(2, 220, 150);
    advance(TOUCH.holdMs);
    expect(out).toEqual([tapOf('gsw', 'previous')]);
  });
});

describe('encoder halves', () => {
  it('left of the vertical centre line is minus, right is plus, once per release, no auto-repeat', () => {
    const { engine, out, advance } = setup();
    engine.pointerDown(1, 299, 50);
    advance(5000);
    engine.pointerUp(1);
    engine.pointerDown(2, 301, 50);
    engine.pointerUp(2);
    expect(out).toEqual([tapOf('enc', 'minus'), tapOf('enc', 'plus')]);
  });

  it('the half is decided where the finger lands, not where it lifts', () => {
    const { engine, out } = setup();
    engine.pointerDown(1, 220, 50);
    engine.pointerMove(1, 225, 50);
    engine.pointerMove(1, 228, 50);
    engine.pointerUp(1);
    expect(out).toEqual([tapOf('enc', 'minus')]);
  });

  it('a drag on the encoder cancels the tap', () => {
    const { engine, out } = setup();
    engine.pointerDown(1, 220, 50);
    engine.pointerMove(1, 220, 70);
    engine.pointerUp(1);
    expect(out).toEqual([]);
  });
});

describe('switch', () => {
  // Switch cell is x 0..200, y 100..200; knob centre (100, 150), square side 100, code knob radius 27.5.
  it('tap on the left half is previous, on the right half next; the host clamps at the ends', () => {
    const { engine, out } = setup();
    engine.pointerDown(1, 20, 150);
    engine.pointerUp(1);
    engine.pointerDown(2, 180, 150);
    engine.pointerUp(2);
    expect(out).toEqual([tapOf('sw', 'previous'), tapOf('sw', 'next')]);
  });

  it('sends previous even when already at the first position (no client-side clamping)', () => {
    const { engine, out } = setup(undefined, { sw: 0 });
    engine.pointerDown(1, 20, 150);
    engine.pointerUp(1);
    expect(out).toEqual([tapOf('sw', 'previous')]);
  });

  it('drag to a position sends one select on release only', () => {
    const { engine, out } = setup();
    engine.pointerDown(1, 100, 130);
    engine.pointerMove(1, 100, 135);
    engine.pointerMove(1, 140, 150);
    expect(out).toEqual([]);
    engine.pointerUp(1);
    expect(out).toEqual([{ type: 'select', widgetId: 'sw', position: 2 }]);
  });

  it('nothing is sent while dragging, and the preview follows the finger', () => {
    const { engine, out, visuals } = setup();
    engine.pointerDown(1, 100, 130);
    engine.pointerMove(1, 60, 150);
    engine.pointerMove(1, 140, 150);
    expect(out).toEqual([]);
    expect(visuals.filter((v) => v?.preview != null).map((v) => v?.preview)).toEqual([2]);
    engine.pointerMove(1, 60, 150);
    expect(visuals.at(-1)?.preview).toBe(0);
  });

  it('sends no select when released on the position already shown', () => {
    const { engine, out } = setup(undefined, { sw: 2 });
    engine.pointerDown(1, 100, 130);
    engine.pointerMove(1, 140, 150);
    engine.pointerUp(1);
    expect(out).toEqual([]);
  });

  it('inside 20 % of the knob radius the preview keeps its last value instead of snapping to the new angle', () => {
    const { engine, out } = setup();
    engine.pointerDown(1, 100, 130);
    engine.pointerMove(1, 140, 150);
    // 3 px left of the centre (dead zone radius is 5.5 px): outside it this angle would pick position 0.
    engine.pointerMove(1, 97, 150);
    engine.pointerUp(1);
    expect(out).toEqual([{ type: 'select', widgetId: 'sw', position: 2 }]);
  });

  it('just outside the dead zone the angle counts again', () => {
    const { engine, out } = setup(undefined, { sw: 1 });
    engine.pointerDown(1, 100, 130);
    engine.pointerMove(1, 140, 150);
    engine.pointerMove(1, 94, 150);
    engine.pointerUp(1);
    expect(out).toEqual([{ type: 'select', widgetId: 'sw', position: 0 }]);
  });

  it('picks the nearest position by circular angle (5 degrees is closer to 350 than to 90)', () => {
    const wrap: Widget = { ...sw, positions: [{ angle: 350 }, { angle: 90 }] };
    const { engine, out } = setup([wrap], { sw: 1 });
    engine.pointerDown(1, 100, 130);
    engine.pointerMove(1, 103.5, 110.2);
    engine.pointerUp(1);
    expect(out).toEqual([{ type: 'select', widgetId: 'sw', position: 0 }]);
  });

  it('a guarded switch still drags normally', () => {
    const { engine, out } = setup([guardedSw]);
    // Cell x 200..300, y 100..200, centre (250, 150).
    engine.pointerDown(1, 250, 130);
    engine.pointerMove(1, 250, 110);
    engine.pointerMove(1, 290, 150);
    engine.pointerUp(1);
    expect(out).toEqual([{ type: 'select', widgetId: 'gsw', position: 2 }]);
  });

  it('uses the layer calibration for the knob centre', () => {
    const shifted: Widget = { ...sw, rotary: { knob: { asset: 'a'.repeat(64), scale: 0.5, offsetX: 0.3 } } };
    const { engine, out } = setup([shifted], { sw: 1 });
    // Square side 100 centred in the 200 x 100 widget (x 0..200, y 100..200): knob centre is (130, 150).
    // The point (115, 150) is west of that centre (position 0) but east of the uncalibrated centre (100, 150).
    engine.pointerDown(1, 130, 120);
    engine.pointerMove(1, 115, 150);
    engine.pointerUp(1);
    expect(out).toEqual([{ type: 'select', widgetId: 'sw', position: 0 }]);
  });
});

describe('dual-action button', () => {
  it('release before 0.5 s is a tap', () => {
    const { engine, out, advance } = setup();
    engine.pointerDown(1, 350, 150);
    advance(TOUCH.holdMs - 1);
    engine.pointerUp(1);
    expect(out).toEqual([tapOf('dual', 'tap')]);
  });

  it('holding to 0.5 s fires hold once, immediately, and the release does nothing more', () => {
    const { engine, out, advance } = setup();
    engine.pointerDown(1, 350, 150);
    advance(TOUCH.holdMs);
    expect(out).toEqual([tapOf('dual', 'hold')]);
    advance(2000);
    engine.pointerUp(1);
    expect(out).toHaveLength(1);
  });

  it('moving beyond the threshold before 0.5 s cancels both tap and hold', () => {
    const { engine, out, advance } = setup();
    engine.pointerDown(1, 350, 150);
    engine.pointerMove(1, 350, 175);
    advance(2000);
    engine.pointerUp(1);
    expect(out).toEqual([]);
  });

  it('without a hold action, any release within the threshold is a tap, however long', () => {
    const { engine, out, advance } = setup([{ ...dual, hold: undefined }]);
    engine.pointerDown(1, 350, 150);
    advance(5000);
    engine.pointerUp(1);
    expect(out).toEqual([tapOf('dual', 'tap')]);
  });

  it('without a tap action, a quick release fires nothing and a hold still fires', () => {
    const { engine, out, advance } = setup([{ ...dual, tap: undefined }]);
    engine.pointerDown(1, 350, 150);
    engine.pointerUp(1);
    expect(out).toEqual([]);
    engine.pointerDown(2, 350, 150);
    advance(TOUCH.holdMs);
    expect(out).toEqual([tapOf('dual', 'hold')]);
  });

  it('ignores the long-press guard: a quick tap still fires on release', () => {
    const { engine, out } = setup([{ ...dual, longPressGuard: true }]);
    engine.pointerDown(1, 350, 150);
    engine.pointerUp(1);
    expect(out).toEqual([tapOf('dual', 'tap')]);
  });
});

describe('cancel and disabled', () => {
  it('pointercancel fires nothing, not even a pending guard or hold, and frees the grid', () => {
    const { engine, out, advance, pending } = setup();
    engine.pointerDown(1, 350, 150);
    engine.pointerCancel(1);
    advance(2000);
    expect(out).toEqual([]);
    expect(pending()).toBe(0);
    expect(engine.pointerDown(2, 50, 50)).toBe(true);
  });

  it('pointercancel during a switch drag sends no select', () => {
    const { engine, out } = setup();
    engine.pointerDown(1, 100, 130);
    engine.pointerMove(1, 140, 150);
    engine.pointerCancel(1);
    expect(out).toEqual([]);
  });

  it('enabled=false ignores new touches', () => {
    const { engine, out } = setup();
    engine.setEnabled(false);
    expect(engine.pointerDown(1, 50, 50)).toBe(false);
    engine.pointerUp(1);
    expect(out).toEqual([]);
  });

  it('enabled=false cancels a touch in progress without firing', () => {
    const { engine, out, advance, visuals } = setup();
    engine.pointerDown(1, 350, 150);
    engine.setEnabled(false);
    advance(2000);
    engine.pointerUp(1);
    expect(out).toEqual([]);
    expect(visuals.at(-1)).toBeNull();
  });
});

describe('visual feedback', () => {
  it('shows the pressed widget and landing half while the finger owns it, and releases afterwards', () => {
    const { engine, visuals } = setup();
    engine.pointerDown(1, 350, 50);
    expect(visuals.at(-1)).toEqual({ widgetId: 'enc', half: 'plus', preview: null });
    engine.pointerUp(1);
    expect(visuals.at(-1)).toBeNull();
  });
});
