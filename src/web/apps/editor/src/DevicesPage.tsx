import { useState } from 'react';
import type { DeviceInfo, DeviceTemplate } from '@bbs/protocol';
import { AddDeviceWizard } from './AddDeviceWizard';
import { api } from './api';
import { validateDeviceName } from './lib/deviceName';
import { buildTemplateRequestMailto } from './lib/mailto';
import { usePoll } from './usePoll';

function DeviceRow({ device, template }: { device: DeviceInfo; template: DeviceTemplate | undefined }) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [confirmRevoke, setConfirmRevoke] = useState(false);
  const [busy, setBusy] = useState(false);

  const startEdit = () => {
    setDraft(device.name);
    setError(null);
    setEditing(true);
  };

  const save = async () => {
    const problem = validateDeviceName(draft);
    if (problem) {
      setError(problem);
      return;
    }
    if (draft === device.name) {
      setEditing(false);
      return;
    }
    setBusy(true);
    try {
      await api.renameDevice(device.id, draft);
      setEditing(false);
    } catch {
      setError('Renaming failed. Try again.');
    } finally {
      setBusy(false);
    }
  };

  const revoke = async () => {
    setBusy(true);
    try {
      await api.revokeDevice(device.id);
    } catch {
      setError('Revoking failed. Try again.');
    } finally {
      setBusy(false);
      setConfirmRevoke(false);
    }
  };

  return (
    <li className="device-row">
      <span
        className={`dot ${device.connected ? 'dot-on' : 'dot-off'}`}
        role="img"
        aria-label={device.connected ? 'Connected' : 'Not connected'}
      />
      <div className="device-main">
        {editing ? (
          <div className="name-edit">
            <input
              className="input"
              value={draft}
              autoFocus
              aria-label="Device name"
              onChange={(e) => {
                setDraft(e.target.value);
                setError(null);
              }}
              onKeyDown={(e) => {
                if (e.key === 'Enter') void save();
                if (e.key === 'Escape') setEditing(false);
              }}
            />
            <button className="btn" disabled={busy} onClick={() => void save()}>Save</button>
            <button className="btn btn-quiet" onClick={() => setEditing(false)}>Cancel</button>
          </div>
        ) : (
          <button className="name-button" title="Click to rename" onClick={startEdit}>
            {device.name}
          </button>
        )}
        {error && <div className="field-error" role="alert">{error}</div>}
        <div className="device-meta">
          Template: {template ? template.name : device.templateId}
          {template?.generic && (
            <>
              {' · '}
              <a href={buildTemplateRequestMailto(template.screen)}>Request official template</a>
            </>
          )}
        </div>
        {device.screenMismatch && (
          <div className="warning">The phone's screen differs from this template.</div>
        )}
      </div>
      <div className="device-actions">
        {confirmRevoke ? (
          <>
            <span>Revoke this device?</span>
            <button className="btn btn-danger" disabled={busy} onClick={() => void revoke()}>Revoke</button>
            <button className="btn btn-quiet" onClick={() => setConfirmRevoke(false)}>Cancel</button>
          </>
        ) : (
          <button className="btn btn-quiet" onClick={() => setConfirmRevoke(true)}>Revoke</button>
        )}
      </div>
    </li>
  );
}

export function DevicesPage() {
  const [devices, setDevices] = useState<DeviceInfo[] | null>(null);
  const [templates, setTemplates] = useState<DeviceTemplate[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [wizardOpen, setWizardOpen] = useState(false);

  usePoll(
    api.devices,
    (d) => {
      setDevices(d.devices);
      setFailed(false);
    },
    () => setFailed(true),
  );
  usePoll(
    api.templates,
    (t) => setTemplates(t.templates),
    () => undefined,
  );

  const byId = new Map((templates ?? []).map((t) => [t.id, t]));

  return (
    <section>
      <div className="section-head">
        <h2>Devices</h2>
        <button className="btn btn-primary" onClick={() => setWizardOpen(true)}>Add Device</button>
      </div>
      {failed && <div className="warning" role="alert">Could not load the device list.</div>}
      {devices && devices.length === 0 && !failed && (
        <p className="muted">No devices yet. Use Add Device to pair your phone.</p>
      )}
      <ul className="device-list">
        {(devices ?? []).map((d) => (
          <DeviceRow key={d.id} device={d} template={byId.get(d.templateId)} />
        ))}
      </ul>
      {wizardOpen && <AddDeviceWizard templates={templates} onClose={() => setWizardOpen(false)} />}
    </section>
  );
}
