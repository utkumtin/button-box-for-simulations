import type {
  DeviceList,
  HostStatus,
  PairingStatus,
  RenameDeviceRequest,
  StartPairingRequest,
  TemplateList,
} from '@bbs/protocol';

/** Non-2xx answer from the editor API. The body is deliberately not kept: it may contain secrets. */
export class ApiError extends Error {
  readonly status: number;
  constructor(status: number) {
    super(`Request failed with status ${status}`);
    this.name = 'ApiError';
    this.status = status;
  }
}

const BASE = '/editor/api';

async function request(method: 'GET' | 'POST', path: string, body?: unknown): Promise<Response> {
  const init: RequestInit =
    method === 'GET'
      ? { method }
      : { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) };
  const res = await fetch(`${BASE}${path}`, init);
  if (!res.ok) throw new ApiError(res.status);
  return res;
}

async function getJson<T>(path: string): Promise<T> {
  return (await (await request('GET', path)).json()) as T;
}

async function postJson<T>(path: string, body?: unknown): Promise<T> {
  return (await (await request('POST', path, body)).json()) as T;
}

async function postNoContent(path: string, body?: unknown): Promise<void> {
  await request('POST', path, body);
}

const deviceId = (id: string) => encodeURIComponent(id);

export const api = {
  status: () => getJson<HostStatus>('/status'),
  devices: () => getJson<DeviceList>('/devices'),
  templates: () => getJson<TemplateList>('/templates'),
  renameDevice: (id: string, name: string) =>
    postNoContent(`/devices/${deviceId(id)}/rename`, { name } satisfies RenameDeviceRequest),
  revokeDevice: (id: string) => postNoContent(`/devices/${deviceId(id)}/revoke`),
  pairing: () => getJson<PairingStatus>('/pairing'),
  startPairing: (templateId?: string) =>
    postJson<PairingStatus>('/pairing/start', (templateId === undefined ? {} : { templateId }) satisfies StartPairingRequest),
  showCode: () => postJson<PairingStatus>('/pairing/code'),
  cancelPairing: () => postJson<PairingStatus>('/pairing/cancel'),
};
