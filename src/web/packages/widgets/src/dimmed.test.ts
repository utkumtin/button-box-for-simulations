import { describe, expect, it } from 'vitest';
import type { Widget } from '@bbs/protocol';
import { isDimmed } from './widgets';

// Why (decisions §10): without telemetry a widget must not pretend to know the game's state. The engine start-stop button has
// no state binding (its lit and LED come from the host's behaviour), so a rule that looked only at the binding would leave it
// showing "electric on" while the game is closed.
const cell = { col: 0, row: 0, colSpan: 1, rowSpan: 1 };
const engine: Widget = { type: 'dualAction', id: 'e', cell, tap: 'ignition-on', hold: 'starter', holdMode: 'fire', behavior: 'engineStartStop' };
const plainDual: Widget = { type: 'dualAction', id: 'd', cell, tap: 'ignition-on', holdMode: 'fire' };
const bound: Widget = { type: 'toggle', id: 't', cell, action: 'beacon', state: { lit: 'truck.light.beacon' } };
const headlights: Widget = {
  type: 'switch',
  id: 'h',
  cell,
  behavior: 'headlights',
  positions: [
    { angle: -30, action: 'light-off' },
    { angle: 0, action: 'light-park' },
    { angle: 30, action: 'light-on' },
    { angle: 60, action: 'light-on' },
  ],
};

describe('isDimmed', () => {
  it('dims the engine start-stop button and bound widgets without telemetry', () => {
    expect(isDimmed(engine, true)).toBe(true);
    expect(isDimmed(bound, true)).toBe(true);
  });

  it('never dims while telemetry is there', () => {
    expect(isDimmed(engine, false)).toBe(false);
    expect(isDimmed(bound, false)).toBe(false);
  });

  it('leaves stateless widgets and commanded knobs alone', () => {
    expect(isDimmed(plainDual, true)).toBe(false);
    expect(isDimmed(headlights, true)).toBe(false);
  });
});
