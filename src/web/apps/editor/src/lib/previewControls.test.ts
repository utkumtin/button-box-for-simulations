import type { Widget } from '@bbs/protocol';
import { describe, expect, it } from 'vitest';
import { litLabel, previewControls } from './previewControls';

const cell = { col: 0, row: 0, colSpan: 1, rowSpan: 1 };
const engine: Widget = { id: 'e', type: 'dualAction', holdMode: 'fire', tap: 'ignition-on', hold: 'starter', behavior: 'engineStartStop', cell };

describe('previewControls', () => {
  // The engine widget has no state binding, but its electric backlight and LED come from the behaviour.
  it('engine start-stop previews electric and LED without a binding', () => {
    expect(previewControls(engine)).toEqual({ lit: true, led: true, position: false });
    expect(litLabel(engine)).toBe('Electric on');
  });

  it('a plain dual-action button previews only what it binds', () => {
    const plain: Widget = { ...engine, behavior: undefined };
    expect(previewControls(plain)).toEqual({ lit: false, led: false, position: false });
    expect(previewControls({ ...plain, state: { lit: 'x' } })).toEqual({ lit: true, led: false, position: false });
    expect(litLabel(plain)).toBe('Lit');
  });

  it('a switch always previews its position, lit only when bound', () => {
    const sw: Widget = { id: 's', type: 'switch', behavior: 'wipers', cell, positions: [{ angle: 0 }, { angle: 30 }] };
    expect(previewControls(sw)).toEqual({ lit: false, led: false, position: true });
    expect(previewControls({ ...sw, state: { lit: 'x' } }).lit).toBe(true);
  });
});
