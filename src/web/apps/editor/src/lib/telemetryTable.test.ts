import { describe, expect, it } from 'vitest';
import { telemetryRows } from './telemetryTable';

const f = (name: string, value: number) => ({ name, value });

describe('telemetryRows', () => {
  it('sorts by name, ignoring case, so the list does not jump around', () => {
    const rows = telemetryRows([f('truck.speed', 1), f('Engine', 2), f('truck.light.beacon', 3)], '');
    expect(rows.map((r) => r.name)).toEqual(['Engine', 'truck.light.beacon', 'truck.speed']);
  });

  it('filters case-insensitively on the name', () => {
    const rows = telemetryRows([f('truck.light.beacon', 1), f('truck.speed', 2), f('LIGHT', 3)], ' LiGht ');
    expect(rows.map((r) => r.name)).toEqual(['LIGHT', 'truck.light.beacon']);
  });

  it('formats values as integers', () => {
    const values = telemetryRows([f('a', 1), f('b', 0), f('c', 2.9), f('d', -3)], '').map((r) => r.value);
    expect(values).toEqual(['1', '0', '2', '-3']);
  });
});
