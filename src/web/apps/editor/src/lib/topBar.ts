import type { DeviceInfo, ProfileSummary, TelemetryView } from '@bbs/protocol';

export function deviceCountLabel(devices: readonly Pick<DeviceInfo, 'connected'>[]): string {
  const n = devices.filter((d) => d.connected).length;
  if (n === 0) return 'No device connected';
  return n === 1 ? '1 device connected' : `${n} devices connected`;
}

export interface TelemetryStatus {
  label: string;
  live: boolean;
}

/** "No telemetry" wins over "Game not listening": without the plugin there is nothing to listen to. */
export function telemetryStatus(t: Pick<TelemetryView, 'available' | 'gameListening'> | null): TelemetryStatus {
  if (t === null || !t.available) return { label: 'No telemetry', live: false };
  if (!t.gameListening) return { label: 'Game not listening', live: false };
  return { label: 'Telemetry live', live: true };
}

export interface ProfileOption {
  id: string;
  name: string;
}

/** Damaged profiles cannot be activated (the host answers 409), so they are not offered. */
export function profileOptions(profiles: readonly ProfileSummary[]): ProfileOption[] {
  return profiles.filter((p) => !p.damaged).map((p) => ({ id: p.id, name: p.name ?? p.id }));
}
