import type { SwitchWidget, Widget } from '@bbs/protocol';
import { describe, expect, it } from 'vitest';
import { normalizeWidget } from '../lib/layoutOps';
import { emptyLayout } from '../lib/testFixtures';
import {
  addPosition,
  layerValue,
  mainAction,
  MAX_POSITIONS,
  removePosition,
  setLayerAsset,
  setLayerValue,
  withLiveWidget,
} from './inspectorOps';

const CELL = { col: 0, row: 0, colSpan: 2, rowSpan: 1 };
const sw = (extra: Partial<SwitchWidget> = {}): SwitchWidget => ({
  id: 'lights',
  type: 'switch',
  cell: CELL,
  positions: [
    { angle: -30, action: 'light-off' },
    { angle: 30, action: 'light-on' },
  ],
  ...extra,
});
const ID = 'a'.repeat(64);

describe('rotary layers', () => {
  it('a new layer gets the renderer default scale (the knob is smaller), so the slider starts where the picture is', () => {
    expect(setLayerAsset(sw(), 'base', ID).rotary?.base).toEqual({ asset: ID, scale: 1 });
    expect(setLayerAsset(sw(), 'knob', ID).rotary?.knob).toEqual({ asset: ID, scale: 0.55 });
  });

  it('changing the image keeps the calibration', () => {
    const calibrated = setLayerValue(setLayerAsset(sw(), 'base', ID), 'base', 'offsetX', 0.2);
    const other = 'b'.repeat(64);
    expect(setLayerAsset(calibrated, 'base', other).rotary?.base).toEqual({ asset: other, scale: 1, offsetX: 0.2 });
  });

  it('removing the image removes the layer; the widget then has no rotary at all after normalizing', () => {
    const removed = setLayerAsset(setLayerAsset(sw(), 'knob', ID), 'knob', undefined);
    expect(removed.rotary).toEqual({});
    expect(normalizeWidget(removed)).not.toHaveProperty('rotary');
  });

  it('clamps scale to 0.1-2 and offsets to -0.5..0.5 (schema limits)', () => {
    const w = setLayerAsset(sw(), 'base', ID);
    expect(setLayerValue(w, 'base', 'scale', 9).rotary?.base?.scale).toBe(2);
    expect(setLayerValue(w, 'base', 'scale', 0).rotary?.base?.scale).toBe(0.1);
    expect(setLayerValue(w, 'base', 'offsetY', -3).rotary?.base?.offsetY).toBe(-0.5);
  });

  it('ignores a calibration for a layer that has no image', () => {
    const w = sw();
    expect(setLayerValue(w, 'highlight', 'scale', 1.5)).toBe(w);
  });

  it('shows the default for values the layer does not have', () => {
    expect(layerValue(undefined, 'knob', 'scale')).toBe(0.55);
    expect(layerValue({ asset: ID }, 'base', 'offsetX')).toBe(0);
    expect(layerValue({ asset: ID, scale: 0.8 }, 'base', 'scale')).toBe(0.8);
  });
});

describe('switch positions', () => {
  it('adds a position after the last angle, up to eight', () => {
    let w: SwitchWidget | null = sw();
    for (let n = w.positions.length; n < MAX_POSITIONS; n++) {
      w = addPosition(w as SwitchWidget);
      expect(w).not.toBeNull();
    }
    expect((w as SwitchWidget).positions).toHaveLength(MAX_POSITIONS);
    expect(addPosition(w as SwitchWidget)).toBeNull();
  });

  it('keeps the new angle inside -180..180', () => {
    const w = sw({ positions: [{ angle: 100, action: 'a' }, { angle: 170, action: 'b' }] });
    expect(addPosition(w)?.positions[2]?.angle).toBe(180);
  });

  it('never goes below two positions', () => {
    expect(removePosition(sw(), 0)).toBeNull();
    const three = addPosition(sw()) as SwitchWidget;
    expect(removePosition(three, 1)?.positions.map((p) => p.angle)).toEqual([-30, 60]);
  });
});

describe('mainAction', () => {
  const base = { id: 'x', cell: CELL };
  it('is the action that decides the default state field', () => {
    expect(mainAction({ ...base, type: 'toggle', action: 'beacon' })).toBe('beacon');
    expect(mainAction({ ...base, type: 'dualAction', holdMode: 'fire', hold: 'starter' })).toBe('starter');
    expect(mainAction({ ...base, type: 'dualAction', holdMode: 'fire', tap: 'a', hold: 'b' })).toBe('a');
    expect(mainAction(sw())).toBe('light-off');
    expect(mainAction({ ...base, type: 'encoder', minus: 'a', plus: 'b' })).toBeUndefined();
    expect(mainAction({ ...base, type: 'toggle', action: '' })).toBeUndefined();
  });
});

describe('withLiveWidget', () => {
  it('shows the live version of one widget without touching the saved layout', () => {
    const layout = { ...emptyLayout(), widgets: [sw()] as Widget[] };
    const live = setLayerAsset(sw(), 'base', ID);
    const shown = withLiveWidget(layout, live);
    expect((shown.widgets[0] as SwitchWidget).rotary?.base?.asset).toBe(ID);
    expect((layout.widgets[0] as SwitchWidget).rotary).toBeUndefined();
  });

  it('is the same layout when there is no live widget or it is gone', () => {
    const layout = { ...emptyLayout(), widgets: [sw()] as Widget[] };
    expect(withLiveWidget(layout, null)).toBe(layout);
    expect(withLiveWidget(layout, sw({ id: 'gone' }))).toBe(layout);
  });
});
