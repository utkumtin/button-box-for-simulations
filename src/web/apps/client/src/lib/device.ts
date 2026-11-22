export interface StoredDevice {
  deviceId: string;
  token: string;
}

export const DEVICE_KEY = 'bbs.device';

const TOKEN_PATTERN = /^[A-Za-z0-9_-]{43}$/;

/** Returns the device only if the stored text has exactly the shape the host issues; otherwise null. */
export function parseDevice(raw: string | null): StoredDevice | null {
  if (raw === null) return null;
  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof value !== 'object' || value === null) return null;
  const { deviceId, token } = value as Record<string, unknown>;
  if (typeof deviceId !== 'string' || deviceId.length === 0 || deviceId.length > 128) return null;
  if (typeof token !== 'string' || !TOKEN_PATTERN.test(token)) return null;
  return { deviceId, token };
}

export function loadDevice(): StoredDevice | null {
  try {
    return parseDevice(localStorage.getItem(DEVICE_KEY));
  } catch {
    return null;
  }
}

export function saveDevice(device: StoredDevice): void {
  try {
    localStorage.setItem(DEVICE_KEY, JSON.stringify({ deviceId: device.deviceId, token: device.token }));
  } catch {
    // Storage unavailable: the device works until the app is closed, then re-pairs.
  }
}

export function clearDevice(): void {
  try {
    localStorage.removeItem(DEVICE_KEY);
  } catch {
    // Nothing to clear.
  }
}
