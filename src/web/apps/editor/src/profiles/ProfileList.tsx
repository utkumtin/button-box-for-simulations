import { useRef } from 'react';
import type { ProfileSummary } from '@bbs/protocol';
import { api } from '../api';
import { GAME_NAMES } from '../lib/catalogView';

export interface ProfileListProps {
  profiles: ProfileSummary[];
  activeId: string | undefined;
  openId: string | null;
  /** The open profile has unsaved changes. */
  openDirty: boolean;
  busy: boolean;
  onOpen: (id: string) => void;
  onNew: () => void;
  onImport: (file: File) => void;
  onDuplicate: (id: string) => void;
  onDelete: (id: string) => void;
  onActivate: (id: string) => void;
}

/** Left column of the Profiles section: all profiles with their actions (decisions §11). */
export function ProfileList(p: ProfileListProps) {
  const file = useRef<HTMLInputElement>(null);
  return (
    <section className="pe-list" aria-label="Profiles">
      <div className="pe-list-head">
        <button type="button" className="btn btn-primary" disabled={p.busy} onClick={p.onNew}>
          New
        </button>
        <button type="button" className="btn" disabled={p.busy} onClick={() => file.current?.click()}>
          Import
        </button>
        <input
          ref={file}
          className="pe-hidden"
          type="file"
          accept=".json,application/json"
          aria-label="Import profile file"
          onChange={(e) => {
            const chosen = e.target.files?.[0];
            e.target.value = '';
            if (chosen) {
              p.onImport(chosen);
            }
          }}
        />
      </div>
      <ul className="pe-profiles">
        {p.profiles.map((s) => {
          const isOpen = s.id === p.openId;
          const isActive = s.id === p.activeId;
          if (s.damaged) {
            return (
              <li key={s.id} className="pe-profile is-damaged">
                <div className="pe-profile-main">
                  <span className="pe-profile-name">{s.name ?? 'Unreadable profile'}</span>
                  <span className="pe-badge pe-badge-bad">Damaged</span>
                </div>
                <p className="muted">
                  This profile file ({s.id}.json in the app's profiles folder) cannot be read. It was left unchanged.
                </p>
                {!isActive && (
                  <div className="pe-profile-actions">
                    <button type="button" className="btn btn-danger" disabled={p.busy} onClick={() => p.onDelete(s.id)}>
                      Delete file
                    </button>
                  </div>
                )}
              </li>
            );
          }
          return (
            <li key={s.id} className={isOpen ? 'pe-profile is-open' : 'pe-profile'}>
              <button type="button" className="pe-profile-open" aria-current={isOpen ? 'true' : undefined} onClick={() => p.onOpen(s.id)}>
                <span className="pe-profile-name">{s.name ?? s.id}</span>
                {isOpen && p.openDirty && <span className="pe-dot" title="Unsaved changes" role="img" aria-label="Unsaved changes" />}
                {isActive && <span className="pe-badge">Active</span>}
                <span className="muted pe-profile-game">{s.game ? GAME_NAMES[s.game] : ''}</span>
              </button>
              <div className="pe-profile-actions">
                {!isActive && (
                  <button type="button" className="btn" disabled={p.busy} onClick={() => p.onActivate(s.id)}>
                    Make active
                  </button>
                )}
                <button type="button" className="btn" disabled={p.busy} onClick={() => p.onDuplicate(s.id)}>
                  Duplicate
                </button>
                <a className="btn" href={api.exportUrl(s.id)} download>
                  Export
                </a>
                <button
                  type="button"
                  className="btn btn-danger"
                  disabled={p.busy || isActive}
                  title={isActive ? 'Switch to another profile first' : undefined}
                  onClick={() => p.onDelete(s.id)}
                >
                  Delete
                </button>
              </div>
            </li>
          );
        })}
        {p.profiles.length === 0 && <li className="muted">No profiles yet. Create one with New.</li>}
      </ul>
    </section>
  );
}
