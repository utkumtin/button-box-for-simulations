import type {
  AssetInfo,
  AssetList,
  CatalogList,
  CreateProfileRequest,
  DeviceList,
  Game,
  HostStatus,
  PairingStatus,
  ProblemList,
  Profile,
  ProfileList,
  RegenerateCaRequest,
  RenameDeviceRequest,
  SettingsStatus,
  StartPairingRequest,
  TelemetryView,
  TemplateList,
  TestPressRequest,
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

/** 422: the host refused a profile or an image and says why. The sentences carry no secrets, so they are kept. */
export class ProblemsError extends ApiError {
  readonly problems: string[];
  constructor(problems: string[]) {
    super(422);
    this.name = 'ProblemsError';
    this.problems = problems;
  }
}

const BASE = '/editor/api';

async function failure(res: Response): Promise<ApiError> {
  if (res.status === 422) {
    try {
      const body = (await res.json()) as ProblemList;
      if (Array.isArray(body.problems)) return new ProblemsError(body.problems.map(String));
    } catch {
      // fall through to the plain error
    }
  }
  return new ApiError(res.status);
}

async function request(method: 'GET' | 'POST', path: string, body?: unknown): Promise<Response> {
  const init: RequestInit =
    method === 'GET'
      ? { method }
      : { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) };
  const res = await fetch(`${BASE}${path}`, init);
  if (!res.ok) throw await failure(res);
  return res;
}

/** Image upload: the raw bytes with their real type (decided by content, decisions §7). */
async function postImage(path: string, bytes: ArrayBuffer, type: AssetUploadType): Promise<Response> {
  const res = await fetch(`${BASE}${path}`, { method: 'POST', headers: { 'Content-Type': type }, body: bytes });
  if (!res.ok) throw await failure(res);
  return res;
}

export type AssetUploadType = 'image/png' | 'image/webp';

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
const seg = (id: string) => encodeURIComponent(id);

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

  // Phase 4 (route table in the Phase 4 task breakdown).
  catalogs: () => getJson<CatalogList>('/catalogs'),
  profiles: () => getJson<ProfileList>('/profiles'),
  profile: (id: string) => getJson<Profile>(`/profiles/${seg(id)}`),
  /** For an `<a download>` link: the host answers with the profile JSON as an attachment. */
  exportUrl: (id: string) => `${BASE}/profiles/${seg(id)}/export`,
  createProfile: (name: string, game: Game) =>
    postJson<Profile>('/profiles/create', { name, game } satisfies CreateProfileRequest),
  /** The file's text is sent as it is; the host validates it and gives the import a new id. */
  importProfile: (json: string) => importText('/profiles/import', json),
  saveProfile: (profile: Profile) => postNoContent(`/profiles/${seg(profile.id)}/save`, profile),
  duplicateProfile: (id: string) => postJson<Profile>(`/profiles/${seg(id)}/duplicate`),
  deleteProfile: (id: string) => postNoContent(`/profiles/${seg(id)}/delete`),
  activateProfile: (id: string) => postNoContent(`/profiles/${seg(id)}/activate`),
  assets: () => getJson<AssetList>('/assets'),
  /** Same-origin image URL for previews; never a blob: URL (decisions §7). */
  assetUrl: (id: string) => `${BASE}/assets/${seg(id)}`,
  uploadAsset: async (bytes: ArrayBuffer, type: AssetUploadType) =>
    (await (await postImage('/assets/upload', bytes, type)).json()) as AssetInfo,
  deleteAsset: (id: string) => postNoContent(`/assets/${seg(id)}/delete`),
  telemetry: () => getJson<TelemetryView>('/telemetry'),
  testPress: (actionId: string) => postNoContent('/test', { actionId } satisfies TestPressRequest),
  settings: () => getJson<SettingsStatus>('/settings'),
  regenerateCa: () => postNoContent('/settings/regenerate-ca', { confirm: 'REGENERATE' } satisfies RegenerateCaRequest),
};

async function importText(path: string, json: string): Promise<Profile> {
  const res = await fetch(`${BASE}${path}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: json });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as Profile;
}
