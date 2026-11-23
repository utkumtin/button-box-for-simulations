import { describe, expect, it } from 'vitest';
import { deviceCountLabel, profileOptions, telemetryStatus } from './topBar';

describe('deviceCountLabel', () => {
  it('counts only connected devices, with correct plural', () => {
    expect(deviceCountLabel([])).toBe('No device connected');
    expect(deviceCountLabel([{ connected: false }])).toBe('No device connected');
    expect(deviceCountLabel([{ connected: true }, { connected: false }])).toBe('1 device connected');
    expect(deviceCountLabel([{ connected: true }, { connected: true }])).toBe('2 devices connected');
  });
});

describe('telemetryStatus', () => {
  it('says no telemetry first, even when the game flag is false too', () => {
    expect(telemetryStatus({ available: false, gameListening: false }).label).toBe('No telemetry');
    expect(telemetryStatus({ available: false, gameListening: true }).label).toBe('No telemetry');
    expect(telemetryStatus(null).label).toBe('No telemetry');
  });

  it('distinguishes not listening and live', () => {
    expect(telemetryStatus({ available: true, gameListening: false })).toEqual({ label: 'Game not listening', live: false });
    expect(telemetryStatus({ available: true, gameListening: true })).toEqual({ label: 'Telemetry live', live: true });
  });
});

describe('profileOptions', () => {
  it('excludes damaged profiles', () => {
    const opts = profileOptions([
      { id: 'a', damaged: false, name: 'Alpha' },
      { id: 'b', damaged: true },
      { id: 'c', damaged: false, name: 'Charlie' },
    ]);
    expect(opts).toEqual([{ id: 'a', name: 'Alpha' }, { id: 'c', name: 'Charlie' }]);
  });
});
