import { useState } from 'react';
import type { DeviceInfo, ProfileList, TelemetryView } from '@bbs/protocol';
import { api } from './api';
import { deviceCountLabel, profileOptions, telemetryStatus } from './lib/topBar';
import { usePoll } from './usePoll';
import './pages.css';

/** Active-profile selector, connected-device count and telemetry status (decisions §11). */
export function TopBarStatus() {
  const [profiles, setProfiles] = useState<ProfileList | null>(null);
  const [devices, setDevices] = useState<DeviceInfo[]>([]);
  const [telemetry, setTelemetry] = useState<TelemetryView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const ignore = () => undefined;
  usePoll(api.profiles, setProfiles, ignore, 1000);
  usePoll(api.devices, (d) => setDevices(d.devices), ignore, 1000);
  usePoll(api.telemetry, setTelemetry, () => setTelemetry(null), 1000);

  const options = profiles ? profileOptions(profiles.profiles) : [];
  const activeId = profiles?.activeId;
  const status = telemetryStatus(telemetry);

  const choose = async (id: string) => {
    if (id === '' || id === activeId) return;
    setBusy(true);
    setError(null);
    try {
      await api.activateProfile(id);
      setProfiles(await api.profiles());
    } catch (e) {
      setError(
        e instanceof Error && 'status' in e && e.status === 409
          ? 'This profile is damaged and cannot be activated.'
          : 'Could not switch the profile.',
      );
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="topbar-status">
      <label className="topbar-profile">
        <span className="muted">Profile</span>
        <select
          className="input"
          value={activeId ?? ''}
          disabled={busy || profiles === null}
          onChange={(e) => void choose(e.target.value)}
        >
          {activeId === undefined && <option value="">No active profile</option>}
          {options.map((o) => (
            <option key={o.id} value={o.id}>{o.name}</option>
          ))}
        </select>
      </label>
      {error && <span className="field-error" role="alert">{error}</span>}
      <span className="topbar-item">{deviceCountLabel(devices)}</span>
      <span className="topbar-item">
        <span className={status.live ? 'dot dot-on' : 'dot dot-off'} aria-hidden="true" />
        {status.label}
      </span>
    </div>
  );
}
