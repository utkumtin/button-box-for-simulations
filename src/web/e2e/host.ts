export interface DeviceRecord {
  deviceId: string;
  token: string;
}

export interface HostInfo {
  port: number;
  controlPort: number;
  secret: string;
  devices: { phone: DeviceRecord; noLayout: DeviceRecord };
}

export interface Received {
  kind: 'press' | 'hold' | 'release';
  name: string;
}

export function hostInfo(): HostInfo {
  const raw = process.env.E2E_HOST;
  if (!raw) throw new Error('E2E_HOST is not set: the global setup did not run.');
  return JSON.parse(raw) as HostInfo;
}

/** The loopback control endpoint of the E2E host (secret header required). */
export class Control {
  constructor(private readonly info: HostInfo) {}

  private async call<T>(method: string, path: string, body?: unknown): Promise<T> {
    const res = await fetch(`http://127.0.0.1:${this.info.controlPort}${path}`, {
      method,
      headers: { 'X-E2E-Secret': this.info.secret, 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!res.ok) throw new Error(`Control ${method} ${path} failed with ${res.status}.`);
    return res.status === 204 ? (undefined as T) : ((await res.json()) as T);
  }

  setState(state: { telemetry?: boolean; gameListening?: boolean; engineNeverStarts?: boolean }): Promise<unknown> {
    return this.call('POST', '/state', state);
  }

  state(): Promise<{ telemetry: boolean; gameListening: boolean; engineNeverStarts: boolean; connected: number }> {
    return this.call('GET', '/state');
  }

  received(): Promise<Received[]> {
    return this.call('GET', '/received');
  }

  /** Closes every phone connection on the host side. */
  drop(): Promise<void> {
    return this.call('POST', '/drop');
  }
}
