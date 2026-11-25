import type { SwitchWidget } from '@bbs/protocol';
import { describe, expect, it } from 'vitest';
import { headlightPreset } from '../lib/presets';
import { CATALOG } from '../lib/testFixtures';
import { addPosition, removePosition, updatePosition } from './inspectorOps';

const cell = { col: 0, row: 0, colSpan: 2, rowSpan: 2 };
const lights = (): SwitchWidget => ({ ...(headlightPreset(CATALOG, new Set()) as Omit<SwitchWidget, 'id' | 'cell'>), id: 'lights', cell });

describe('position ops on a behaviour switch', () => {
  // The count is part of the behaviour: the host refuses a headlights switch that is not exactly 4 positions.
  it('refuses to add or remove a position', () => {
    expect(addPosition(lights())).toBeNull();
    expect(removePosition(lights(), 1)).toBeNull();
  });

  it('plain switches still add and remove', () => {
    const plain: SwitchWidget = {
      ...lights(),
      behavior: undefined,
      positions: [
        { angle: 0, action: 'a' },
        { angle: 30, action: 'b' },
        { angle: 60, action: 'c' },
      ],
    };
    expect(addPosition(plain)?.positions).toHaveLength(4);
    expect(removePosition(plain, 0)?.positions).toHaveLength(2);
  });

  it('label and angle edits keep working', () => {
    const next = updatePosition(lights(), 0, (p) => ({ ...p, label: 'X', angle: -45 }));
    expect(next.positions[0]).toMatchObject({ label: 'X', angle: -45 });
  });
});
