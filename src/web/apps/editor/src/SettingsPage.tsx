import { useState } from 'react';
import type { FirewallState, ScsGame, ScsPluginStatus, SettingsStatus } from '@bbs/protocol';
import { api, ApiError, ProblemsError } from './api';
import { gameRow, installMessage, NOT_INCLUDED_TEXT, type InstallMessage } from './lib/scsPlugin';
import { groupFingerprint } from './lib/fingerprint';
import { usePoll } from './usePoll';
import './pages.css';

const CONFIRM_WORD = 'REGENERATE';

function Fingerprint({ hex }: { hex: string }) {
  let groups;
  try {
    groups = groupFingerprint(hex);
  } catch {
    return <p className="warning" role="alert">The host sent an invalid fingerprint.</p>;
  }
  return (
    <p className="fingerprint" aria-label="Certificate fingerprint">
      {groups.map((g, i) => (
        <span key={i} className={g.highlight ? 'fp-group fp-edge' : 'fp-group'}>{g.text}</span>
      ))}
    </p>
  );
}

function firewallText(state: FirewallState): string {
  if (state === 'present') return 'Rule present';
  if (state === 'missing') return 'Rule missing – phones on the network cannot connect';
  return 'Unknown';
}

function regenerateError(e: unknown): string {
  if (e instanceof ApiError && e.status === 503) {
    return 'The certificate authority is not available, so it cannot be regenerated.';
  }
  return 'Could not create a new certificate. Nothing was changed.';
}

function RegenerateDialog({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const [typed, setTyped] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.regenerateCa();
      onDone();
    } catch (e) {
      setError(regenerateError(e));
      setBusy(false);
    }
  };

  return (
    <div className="overlay">
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="settings-regen-title">
        <h3 id="settings-regen-title">Regenerate certificate</h3>
        <ul>
          <li>A new certificate authority is created.</li>
          <li>Every phone must download and install the new certificate profile and pair again.</li>
          <li>All paired devices are removed now.</li>
        </ul>
        <label className="settings-confirm">
          <span>Type {CONFIRM_WORD} to continue</span>
          <input
            className="input"
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            autoComplete="off"
            spellCheck={false}
          />
        </label>
        {error && <p className="field-error" role="alert">{error}</p>}
        <div className="row-actions">
          <button type="button" className="btn btn-danger" disabled={typed !== CONFIRM_WORD || busy} onClick={() => void confirm()}>
            Regenerate certificate
          </button>
          <button type="button" className="btn" disabled={busy} onClick={onClose}>Cancel</button>
        </div>
      </div>
    </div>
  );
}

/** Per game state and Install/Update (decisions §11 Phase 5, §12). Buttons wait while a request runs. */
function ScsPanel({ plugin }: { plugin: ScsPluginStatus | null }) {
  const [busy, setBusy] = useState<ScsGame | null>(null);
  const [message, setMessage] = useState<(InstallMessage & { game: ScsGame }) | null>(null);

  const install = async (game: ScsGame) => {
    setBusy(game);
    setMessage(null);
    try {
      await api.installScsPlugin(game);
      setMessage({ game, ...installMessage(game, 204) });
    } catch (e) {
      const status = e instanceof ApiError ? e.status : 0;
      setMessage({ game, ...installMessage(game, status, e instanceof ProblemsError ? e.problems : []) });
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="settings-panel">
      <h3>SCS plugin</h3>
      {plugin === null ? (
        <p className="muted">Loading…</p>
      ) : !plugin.included ? (
        <p className="muted">{NOT_INCLUDED_TEXT}</p>
      ) : (
        plugin.games.map((status) => {
          const row = gameRow(status);
          return (
            <div key={row.game} className="settings-game">
              <p>
                <strong>{row.name}</strong>: {row.state}
                {row.connected && ' – Connected'}
              </p>
              {row.installDir && <p className="muted">{row.installDir}</p>}
              {row.button && (
                <button type="button" className="btn" disabled={busy !== null} onClick={() => void install(row.game)}>
                  {row.button}
                </button>
              )}
              {message?.game === row.game && (
                <p className={message.ok ? undefined : 'field-error'} role={message.ok ? 'status' : 'alert'}>
                  {message.text}
                </p>
              )}
            </div>
          );
        })
      )}
    </div>
  );
}

/** Status panels (decisions §11, Phase 4 scope). */
export function SettingsPage() {
  const [settings, setSettings] = useState<SettingsStatus | null>(null);
  const [failed, setFailed] = useState(false);
  const [dialog, setDialog] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  usePoll(
    api.settings,
    (s) => {
      setSettings(s);
      setFailed(false);
    },
    () => setFailed(true),
    2000,
  );

  const cert = settings?.certificate;

  return (
    <section className="settings-page">
      <h2>Settings</h2>
      {failed && <p className="field-error" role="alert">Cannot read the settings from the host.</p>}

      <div className="settings-panel">
        <h3>Certificate</h3>
        {settings === null ? (
          <p className="muted">Loading…</p>
        ) : cert?.available ? (
          <>
            <p>Certificate authority available.</p>
            {cert.fingerprint && <Fingerprint hex={cert.fingerprint} />}
          </>
        ) : (
          <p className="warning">Certificate authority not available.</p>
        )}
        {notice && <p role="status">{notice}</p>}
        <button type="button" className="btn btn-danger" disabled={!cert?.available} onClick={() => setDialog(true)}>
          Regenerate certificate…
        </button>
      </div>

      <div className="settings-panel">
        <h3>Firewall</h3>
        <p className={settings?.firewall === 'missing' ? 'warning' : undefined}>
          {settings ? firewallText(settings.firewall) : 'Unknown'}
        </p>
        <p className="muted">Setting up the firewall rule from the app is not available yet.</p>
      </div>

      <div className="settings-panel">
        <h3>vJoy</h3>
        <p className="muted">Available in a later version.</p>
      </div>
      <ScsPanel plugin={settings?.scsPlugin ?? null} />
      <div className="settings-panel">
        <h3>Updates</h3>
        <p className="muted">Available in a later version.</p>
      </div>

      {dialog && (
        <RegenerateDialog
          onClose={() => setDialog(false)}
          onDone={() => {
            setDialog(false);
            setNotice('New certificate created. Pair your devices again.');
          }}
        />
      )}
    </section>
  );
}
