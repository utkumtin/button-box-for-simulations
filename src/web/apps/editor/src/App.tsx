import { useState } from 'react';
import type { HostStatus } from '@bbs/protocol';
import { api } from './api';
import { AssetsPage } from './AssetsPage';
import { DevicesPage } from './DevicesPage';
import { ProfilesPage } from './ProfilesPage';
import { SettingsPage } from './SettingsPage';
import { TelemetryPage } from './TelemetryPage';
import { TopBarStatus } from './TopBarStatus';
import { usePoll } from './usePoll';

/** Editor sections (decisions §11). */
export type Section = 'profiles' | 'devices' | 'assets' | 'telemetry' | 'settings';

const SECTIONS: { id: Section; label: string }[] = [
  { id: 'profiles', label: 'Profiles' },
  { id: 'devices', label: 'Devices' },
  { id: 'assets', label: 'Assets' },
  { id: 'telemetry', label: 'Telemetry' },
  { id: 'settings', label: 'Settings' },
];

function StatusBanner({ status, unreachable }: { status: HostStatus | null; unreachable: boolean }) {
  if (unreachable) return <div className="banner banner-error" role="alert">Cannot reach the host.</div>;
  if (!status) return null;
  if (status.lanServer === 'portInUse') {
    return (
      <div className="banner banner-error" role="alert">
        Port 443 is in use by {status.portOwner ?? 'another program'} – phones cannot connect
      </div>
    );
  }
  if (status.lanServer === 'noLanAddress') {
    return <div className="banner banner-error" role="alert">No local network found</div>;
  }
  // The files are left untouched on purpose: a new certificate authority or an empty device list would force every
  // phone to pair again (decisions §9). The user has to repair or remove the file, then restart the app.
  if (status.lanServer === 'caUnavailable') {
    return (
      <div className="banner banner-error" role="alert">
        The certificate authority cannot be loaded – phones cannot connect. Restart the app; if it persists, the files in the
        app's ca folder are damaged or belong to another Windows user.
      </div>
    );
  }
  if (status.lanServer === 'devicesUnavailable') {
    return (
      <div className="banner banner-error" role="alert">
        The list of paired devices cannot be read (devices.json) – phones cannot connect. The file was left unchanged.
      </div>
    );
  }
  if (status.lanServer === 'startFailed') {
    return (
      <div className="banner banner-error" role="alert">
        The phone server could not start. Retrying…
      </div>
    );
  }
  return null;
}

function statusLabel(status: HostStatus | null): string {
  if (!status) return 'Connecting to host…';
  switch (status.lanServer) {
    case 'starting':
      return 'Starting…';
    case 'running':
      return `${status.mdnsName}.local`;
    default:
      return 'Phones cannot connect';
  }
}

export function App() {
  const [status, setStatus] = useState<HostStatus | null>(null);
  const [unreachable, setUnreachable] = useState(false);
  const [section, setSection] = useState<Section>('profiles');

  usePoll(
    api.status,
    (s) => {
      setStatus(s);
      setUnreachable(false);
    },
    () => setUnreachable(true),
  );

  return (
    <div className="app">
      <header className="topbar">
        <h1 className="product">Button Box Editor</h1>
        <TopBarStatus />
        <span className="host-status">{statusLabel(status)}</span>
      </header>
      <nav className="sections" aria-label="Sections">
        {SECTIONS.map((s) => (
          <button
            key={s.id}
            type="button"
            className={s.id === section ? 'section-tab is-current' : 'section-tab'}
            aria-current={s.id === section ? 'page' : undefined}
            onClick={() => setSection(s.id)}
          >
            {s.label}
          </button>
        ))}
      </nav>
      <StatusBanner status={status} unreachable={unreachable} />
      <main className={section === 'profiles' ? 'content content-wide' : 'content'}>
        {/* Kept mounted while another section is shown: the unsaved draft must not vanish when the user looks at Assets. */}
        <div hidden={section !== 'profiles'}>
          <ProfilesPage />
        </div>
        {section === 'devices' && <DevicesPage />}
        {section === 'assets' && <AssetsPage />}
        {section === 'telemetry' && <TelemetryPage />}
        {section === 'settings' && <SettingsPage />}
      </main>
    </div>
  );
}
