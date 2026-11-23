import { useReducer, useState } from 'react';
import type { AssetInfo, CatalogList, DeviceInfo, DeviceTemplate, Game, Profile, ProfileList as ProfileListData } from '@bbs/protocol';
import { api, ProblemsError } from './api';
import { gameChoices } from './lib/catalogView';
import {
  createDraft,
  currentProfile,
  draftReducer,
  isDirty,
  jsonEqual,
  type Draft,
  type DraftAction,
} from './lib/draft';
import { ConfirmDialog, type ConfirmRequest } from './profiles/ConfirmDialog';
import { profileErrorMessages } from './profiles/errors';
import { LayoutEditor } from './profiles/LayoutEditor';
import { NewProfileDialog } from './profiles/NewProfileDialog';
import { ProfileList } from './profiles/ProfileList';
import { usePoll } from './usePoll';
import './profiles/profiles.css';

type Session = Draft | null;
type SessionAction = DraftAction | { type: 'close' };

/** The open profile's editing session, or none. Actions for a session that is not open are dropped. */
function sessionReducer(state: Session, action: SessionAction): Session {
  if (action.type === 'close') {
    return null;
  }
  if (action.type === 'load') {
    return createDraft(action.profile);
  }
  return state ? draftReducer(state, action) : null;
}

/** Keeps the old object when a poll brings the same data, so the editor does not recompute for nothing. */
function keepIfSame<T>(old: T, next: T): T {
  return jsonEqual(old, next) ? old : next;
}

/** Profiles section (decisions §11): profile list, template tabs, canvas, inspector, test mode. */
export function ProfilesPage() {
  const [list, setList] = useState<ProfileListData | null>(null);
  const [templates, setTemplates] = useState<DeviceTemplate[] | null>(null);
  const [catalogs, setCatalogs] = useState<CatalogList | null>(null);
  const [devices, setDevices] = useState<DeviceInfo[]>([]);
  const [assets, setAssets] = useState<AssetInfo[] | null>(null);
  const [session, dispatch] = useReducer(sessionReducer, null);
  const [errors, setErrors] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [saving, setSaving] = useState(false);
  const [hostProblems, setHostProblems] = useState<{ profile: Profile; problems: string[] } | null>(null);
  const [newOpen, setNewOpen] = useState(false);
  const [newErrors, setNewErrors] = useState<string[]>([]);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);

  const ignore = () => undefined;
  usePoll(api.profiles, (l) => setList((old) => (old ? keepIfSame(old, l) : l)), ignore, 2000);
  // Host data that changes rarely (templates, catalogs) and what the editor needs next to the profile.
  usePoll(
    () => Promise.all([api.templates(), api.catalogs(), api.devices(), api.assets()]),
    ([t, c, d, a]) => {
      setTemplates((old) => (old ? keepIfSame(old, t.templates) : t.templates));
      setCatalogs((old) => (old ? keepIfSame(old, c) : c));
      setDevices((old) => keepIfSame(old, d.devices));
      setAssets((old) => (old ? keepIfSame(old, a.assets) : a.assets));
    },
    ignore,
    5000,
  );

  const refresh = async () => {
    try {
      setList(await api.profiles());
    } catch {
      // The poll shows the next state; a failed refresh changes nothing.
    }
  };

  const dirty = session !== null && isDirty(session);

  /** Runs `action` at once, or after "Discard unsaved changes?" when the open profile has unsaved changes. */
  const guardDiscard = (action: () => void) => {
    if (!dirty) {
      action();
      return;
    }
    setConfirm({
      title: 'Discard unsaved changes?',
      message: 'The open profile has changes that are not saved. They are lost when you switch.',
      confirmLabel: 'Discard',
      danger: true,
      onConfirm: () => {
        setConfirm(null);
        action();
      },
    });
  };

  /** A mutation of the profile files: one at a time, failures shown at the top. */
  const mutate = async (work: () => Promise<void>) => {
    setBusy(true);
    setErrors([]);
    try {
      await work();
    } catch (e) {
      setErrors(profileErrorMessages(e));
    } finally {
      setBusy(false);
    }
  };

  const open = (id: string) => {
    if (session && currentProfile(session).id === id) {
      return;
    }
    guardDiscard(() => {
      void mutate(async () => {
        dispatch({ type: 'load', profile: await api.profile(id) });
      });
    });
  };

  const create = async (name: string, game: Game) => {
    setBusy(true);
    setNewErrors([]);
    try {
      const created = await api.createProfile(name, game);
      setNewOpen(false);
      await refresh();
      guardDiscard(() => dispatch({ type: 'load', profile: created }));
    } catch (e) {
      setNewErrors(profileErrorMessages(e));
    } finally {
      setBusy(false);
    }
  };

  const importFile = (file: File) => {
    void mutate(async () => {
      // The import appears as a new profile in the list. It is not opened, so unsaved work in the open one is never at risk.
      await api.importProfile(await file.text());
      await refresh();
    });
  };

  const duplicate = (id: string) => {
    void mutate(async () => {
      await api.duplicateProfile(id);
      await refresh();
    });
  };

  const activate = (id: string) => {
    void mutate(async () => {
      await api.activateProfile(id);
      await refresh();
    });
  };

  const remove = (id: string) => {
    const name = list?.profiles.find((p) => p.id === id)?.name ?? id;
    setConfirm({
      title: 'Delete profile?',
      message: `Delete "${name}"? This cannot be undone.`,
      confirmLabel: 'Delete',
      danger: true,
      onConfirm: () => {
        setConfirm(null);
        void mutate(async () => {
          await api.deleteProfile(id);
          if (session && currentProfile(session).id === id) {
            dispatch({ type: 'close' });
          }
          await refresh();
        });
      },
    });
  };

  const save = async () => {
    if (!session) {
      return;
    }
    const profile = currentProfile(session);
    setSaving(true);
    setErrors([]);
    setHostProblems(null);
    try {
      await api.saveProfile(profile);
      dispatch({ type: 'saved', profile });
      await refresh();
    } catch (e) {
      if (e instanceof ProblemsError) {
        setHostProblems({ profile, problems: e.problems });
      } else {
        setErrors(profileErrorMessages(e));
      }
    } finally {
      setSaving(false);
    }
  };

  const profile = session ? currentProfile(session) : null;
  const catalog = profile && catalogs ? (catalogs.catalogs.find((c) => c.game === profile.game) ?? null) : null;
  const shownHostProblems = hostProblems && profile && hostProblems.profile === profile ? hostProblems.problems : [];

  return (
    <div className="pe-page">
      <ProfileList
        profiles={list?.profiles ?? []}
        activeId={list?.activeId}
        openId={profile?.id ?? null}
        openDirty={dirty}
        busy={busy}
        onOpen={open}
        onNew={() => {
          setNewErrors([]);
          setNewOpen(true);
        }}
        onImport={importFile}
        onDuplicate={duplicate}
        onDelete={remove}
        onActivate={activate}
      />
      <div className="pe-main">
        {errors.length > 0 && (
          <div className="pe-errors" role="alert">
            <ul>
              {errors.map((m, i) => (
                <li key={i}>{m}</li>
              ))}
            </ul>
            <button type="button" className="btn btn-quiet" onClick={() => setErrors([])}>
              Dismiss
            </button>
          </div>
        )}
        {session && profile ? (
          templates && catalogs ? (
            <LayoutEditor
              key={profile.id}
              draft={session}
              dispatch={dispatch}
              templates={templates}
              catalog={catalog}
              assets={assets}
              devices={devices}
              isActive={list?.activeId === profile.id}
              saving={saving}
              hostProblems={shownHostProblems}
              onSave={() => void save()}
            />
          ) : (
            <p className="muted pe-center">Loading devices and catalogs...</p>
          )
        ) : (
          <p className="muted pe-center">Choose a profile on the left, or create a new one.</p>
        )}
      </div>
      {newOpen && catalogs && (
        <NewProfileDialog
          choices={gameChoices(catalogs)}
          busy={busy}
          errors={newErrors}
          onCreate={(name, game) => void create(name, game)}
          onCancel={() => setNewOpen(false)}
        />
      )}
      {confirm && <ConfirmDialog request={confirm} onCancel={() => setConfirm(null)} />}
    </div>
  );
}
