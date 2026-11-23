import { useCallback, useEffect, useRef, useState } from 'react';
import type { AssetInfo, ProfileSummary } from '@bbs/protocol';
import { api, ApiError, ProblemsError } from './api';
import { sniffImage } from './lib/imageSniff';
import { usePoll } from './usePoll';
import './pages.css';

interface UploadResult {
  file: string;
  messages: string[];
}

function formatBytes(n: number): string {
  if (n >= 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`;
  return `${Math.max(1, Math.round(n / 1024))} KB`;
}

function uploadMessages(e: unknown): string[] {
  if (e instanceof ProblemsError) return e.problems;
  if (e instanceof ApiError) {
    if (e.status === 413) return ['The image is too large.'];
    if (e.status === 415) return ['Only PNG and WebP images can be imported.'];
    if (e.status === 503) return ['The host cannot store images right now.'];
  }
  return ['The upload failed.'];
}

async function uploadOne(file: File): Promise<UploadResult> {
  const bytes = await file.arrayBuffer();
  const sniffed = sniffImage(new Uint8Array(bytes));
  if (!sniffed.ok) return { file: file.name, messages: [sniffed.message] };
  try {
    await api.uploadAsset(bytes, sniffed.type);
    return { file: file.name, messages: [] };
  } catch (e) {
    return { file: file.name, messages: uploadMessages(e) };
  }
}

function DeleteDialog({
  asset,
  busy,
  error,
  onConfirm,
  onCancel,
}: {
  asset: AssetInfo;
  busy: boolean;
  error: string | null;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <div className="overlay">
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="assets-delete-title">
        <h3 id="assets-delete-title">Delete image</h3>
        <p>Delete image {asset.id.slice(0, 8)}? This cannot be undone.</p>
        {error && <p className="field-error" role="alert">{error}</p>}
        <div className="row-actions">
          <button type="button" className="btn btn-danger" disabled={busy} onClick={onConfirm}>Delete</button>
          <button type="button" className="btn" disabled={busy} onClick={onCancel}>Cancel</button>
        </div>
      </div>
    </div>
  );
}

/** Asset library: upload, list, delete (decisions §7). */
export function AssetsPage() {
  const [assets, setAssets] = useState<AssetInfo[]>([]);
  const [profiles, setProfiles] = useState<ProfileSummary[]>([]);
  const [loadError, setLoadError] = useState(false);
  const [results, setResults] = useState<UploadResult[]>([]);
  const [uploading, setUploading] = useState(false);
  const [toDelete, setToDelete] = useState<AssetInfo | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const alive = useRef(true);

  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);

  const refresh = useCallback(async () => {
    try {
      const [a, p] = await Promise.all([api.assets(), api.profiles()]);
      if (!alive.current) return;
      setAssets(a.assets);
      setProfiles(p.profiles);
      setLoadError(false);
    } catch {
      if (alive.current) setLoadError(true);
    }
  }, []);

  // Polling also does the first load and picks up profile changes (the "Used by" column).
  usePoll(
    () => Promise.all([api.assets(), api.profiles()]),
    ([a, p]) => {
      setAssets(a.assets);
      setProfiles(p.profiles);
      setLoadError(false);
    },
    () => setLoadError(true),
    2000,
  );

  const profileName = (id: string) => profiles.find((p) => p.id === id)?.name ?? id;

  const onFiles = async (files: FileList | null) => {
    if (!files || files.length === 0) return;
    const picked = [...files];
    if (fileInput.current) fileInput.current.value = '';
    setUploading(true);
    const out: UploadResult[] = [];
    for (const file of picked) out.push(await uploadOne(file));
    if (!alive.current) return;
    setResults(out.filter((r) => r.messages.length > 0));
    setUploading(false);
    await refresh();
  };

  const confirmDelete = async () => {
    if (!toDelete) return;
    setDeleting(true);
    setDeleteError(null);
    try {
      await api.deleteAsset(toDelete.id);
      setToDelete(null);
      await refresh();
    } catch (e) {
      setDeleteError(
        e instanceof ApiError && e.status === 409 ? 'This image is still used by a profile.' : 'Could not delete the image.',
      );
      await refresh();
    } finally {
      if (alive.current) setDeleting(false);
    }
  };

  return (
    <section>
      <div className="section-head">
        <h2>Assets</h2>
        <label className="btn btn-primary assets-upload">
          {uploading ? 'Uploading…' : 'Upload images'}
          <input
            ref={fileInput}
            className="assets-file"
            type="file"
            accept="image/png,image/webp"
            multiple
            disabled={uploading}
            aria-label="Upload images"
            onChange={(e) => void onFiles(e.target.files)}
          />
        </label>
      </div>
      <p className="muted">Square PNG or WebP with transparency, 512–2048 px, up to 4 MB.</p>

      {results.map((r, i) => (
        <div key={i} className="field-error" role="alert">
          <strong>{r.file}</strong>
          <ul>
            {r.messages.map((m, j) => (
              <li key={j}>{m}</li>
            ))}
          </ul>
        </div>
      ))}
      {loadError && <p className="field-error" role="alert">Cannot load the assets from the host.</p>}

      {assets.length === 0 && !loadError ? (
        <p className="muted">No images yet.</p>
      ) : (
        <ul className="assets-list">
          {assets.map((a) => {
            const used = a.usedBy.length > 0;
            return (
              <li key={a.id} className="assets-row">
                <img className="assets-thumb" src={api.assetUrl(a.id)} alt={`Image ${a.id.slice(0, 8)}`} />
                <div className="assets-info">
                  <div><code>{a.id.slice(0, 8)}</code> · {a.type.toUpperCase()}</div>
                  <div className="muted">{a.size} × {a.size} px · {formatBytes(a.bytes)}</div>
                  <div className="muted">Used by: {used ? a.usedBy.map(profileName).join(', ') : 'nothing'}</div>
                </div>
                <button
                  type="button"
                  className="btn"
                  disabled={used}
                  title={used ? 'Still used by a profile' : undefined}
                  onClick={() => {
                    setDeleteError(null);
                    setToDelete(a);
                  }}
                >
                  Delete
                </button>
              </li>
            );
          })}
        </ul>
      )}

      {toDelete && (
        <DeleteDialog
          asset={toDelete}
          busy={deleting}
          error={deleteError}
          onConfirm={() => void confirmDelete()}
          onCancel={() => setToDelete(null)}
        />
      )}
    </section>
  );
}
