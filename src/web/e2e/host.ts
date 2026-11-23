export interface DeviceRecord {
  deviceId: string;
  token: string;
}

export interface HostInfo {
  port: number;
  controlPort: number;
  secret: string;
  /** The loopback editor server (HTTP) and the value of its session cookie. */
  editorPort: number;
  editorSecret: string;
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

export interface E2EWidget {
  id: string;
  label?: string;
  cell: { col: number; row: number; colSpan: number; rowSpan: number };
  [key: string]: unknown;
}

export interface E2ELayout {
  templateId: string;
  grid: { columns: number; rows: number };
  widgets: E2EWidget[];
  [key: string]: unknown;
}

/** The parts of a profile the tests read and change; everything else is passed through untouched. */
export interface E2EProfile {
  id: string;
  name: string;
  layouts: E2ELayout[];
  [key: string]: unknown;
}

export interface ProfileListing {
  activeId?: string | null;
  profiles: { id: string; name?: string | null; damaged: boolean }[];
}

export const PHONE_TEMPLATE = 'iphone-14-pro';

/**
 * The editor API as the editor's own page calls it, with the session cookie from the ready line (the user's browser never has
 * it, so the tests set it themselves) and the matching Origin on every POST (decisions section 9).
 */
export class EditorApi {
  private readonly origin: string;

  constructor(private readonly info: HostInfo) {
    this.origin = `http://127.0.0.1:${info.editorPort}`;
  }

  private async call(method: 'GET' | 'POST', path: string, body?: unknown): Promise<Response> {
    const headers: Record<string, string> = { Cookie: `bbs_editor=${this.info.editorSecret}` };
    if (method === 'POST') {
      headers.Origin = this.origin;
      headers['Content-Type'] = 'application/json';
    }
    const res = await fetch(`${this.origin}/editor/api${path}`, {
      method,
      headers,
      body: method === 'POST' ? JSON.stringify(body ?? {}) : undefined,
    });
    if (!res.ok) throw new Error(`Editor API ${method} ${path} failed with ${res.status}.`);
    return res;
  }

  async profiles(): Promise<ProfileListing> {
    return (await (await this.call('GET', '/profiles')).json()) as ProfileListing;
  }

  async profile(id: string): Promise<E2EProfile> {
    return (await (await this.call('GET', `/profiles/${encodeURIComponent(id)}`)).json()) as E2EProfile;
  }

  async save(profile: E2EProfile): Promise<void> {
    await this.call('POST', `/profiles/${encodeURIComponent(profile.id)}/save`, profile);
  }

  async activate(id: string): Promise<void> {
    await this.call('POST', `/profiles/${encodeURIComponent(id)}/activate`);
  }

  async create(name: string, game: string): Promise<E2EProfile> {
    return (await (await this.call('POST', '/profiles/create', { name, game })).json()) as E2EProfile;
  }

  async duplicate(id: string): Promise<E2EProfile> {
    return (await (await this.call('POST', `/profiles/${encodeURIComponent(id)}/duplicate`)).json()) as E2EProfile;
  }

  async remove(id: string): Promise<void> {
    await this.call('POST', `/profiles/${encodeURIComponent(id)}/delete`);
  }

  /** The active profile's id; the host always has one after the first-run seed. */
  async activeId(): Promise<string> {
    const { activeId } = await this.profiles();
    if (!activeId) throw new Error('The host has no active profile.');
    return activeId;
  }
}
