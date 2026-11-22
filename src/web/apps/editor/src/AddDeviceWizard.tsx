import { useEffect, useState } from 'react';
import type { DeviceTemplate, PairingStatus } from '@bbs/protocol';
import { api } from './api';
import { formatCountdown, remainingMs } from './lib/countdown';
import { groupFingerprint } from './lib/fingerprint';
import { QrCode } from './QrCode';
import { usePoll } from './usePoll';

const ATTEMPTS_WARN_BELOW = 5;

function useNow(active: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    const id = setInterval(() => setNow(Date.now()), 250);
    return () => clearInterval(id);
  }, [active]);
  return now;
}

function Fingerprint({ hex }: { hex: string }) {
  let groups;
  try {
    groups = groupFingerprint(hex);
  } catch {
    return <p className="warning" role="alert">The host sent an invalid fingerprint. Cancel and try again.</p>;
  }
  return (
    <p className="fingerprint" aria-label="Certificate fingerprint">
      {groups.map((g, i) => (
        <span key={i} className={g.highlight ? 'fp-group fp-edge' : 'fp-group'}>{g.text}</span>
      ))}
    </p>
  );
}

function CodeView({ status, onNewCode, busy }: { status: PairingStatus; onNewCode: () => void; busy: boolean }) {
  const now = useNow(true);
  const left = status.codeExpiresAt ? remainingMs(status.codeExpiresAt, now) : 0;
  return (
    <div>
      <h3>Enter this code on your iPhone</h3>
      <p className="code" aria-label="Pairing code">{status.code}</p>
      <p className="muted">
        Expires in <strong>{formatCountdown(left)}</strong>
        {status.attemptsLeft !== undefined && status.attemptsLeft < ATTEMPTS_WARN_BELOW && (
          <> · {status.attemptsLeft} {status.attemptsLeft === 1 ? 'attempt' : 'attempts'} left</>
        )}
      </p>
      {left === 0 && (
        <button className="btn" disabled={busy} onClick={onNewCode}>Show a new code</button>
      )}
    </div>
  );
}

interface FlowProps {
  initial: PairingStatus;
  onDecline: () => void;
  onDone: () => void;
}

function PairingFlow({ initial, onDecline, onDone }: FlowProps) {
  const [status, setStatus] = useState(initial);
  const [confirmed, setConfirmed] = useState(false);
  const [lost, setLost] = useState(false);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState(false);

  usePoll(
    api.pairing,
    (s) => {
      setStatus(s);
      setLost(false);
    },
    () => setLost(true),
  );

  const act = async (fn: () => Promise<PairingStatus>) => {
    setBusy(true);
    setActionError(false);
    try {
      setStatus(await fn());
    } catch {
      setActionError(true);
    } finally {
      setBusy(false);
    }
  };

  const decline = async () => {
    try {
      await api.cancelPairing();
    } catch {
      // The warning is shown either way; the host closes the window on its own timeout.
    }
    onDecline();
  };

  let body;
  if (status.state === 'paired') {
    body = (
      <div>
        <h3>Device paired</h3>
        <p>Your iPhone is connected.</p>
        {status.screenMismatch && (
          <p className="warning">
            The screen size your iPhone reported differs from the model you chose. Layouts may not fit exactly.
          </p>
        )}
        <button className="btn btn-primary" onClick={onDone}>Done</button>
      </div>
    );
  } else if (status.state === 'failed') {
    if (status.failure === 'portInUse') {
      body = (
        <p className="warning" role="alert">
          The pairing page cannot start because port 80 and its fallback are in use. Close the program using them and try again.
        </p>
      );
    } else {
      body = (
        <div>
          <p className="warning" role="alert">
            {status.failure === 'codeBurned'
              ? 'Too many wrong attempts. This code can no longer be used.'
              : 'The code has expired.'}
          </p>
          <button className="btn btn-primary" disabled={busy} onClick={() => void act(api.showCode)}>
            Show a new code
          </button>
        </div>
      );
    }
  } else if (status.state === 'codeShown') {
    body = <CodeView status={status} busy={busy} onNewCode={() => void act(api.showCode)} />;
  } else if (status.state === 'caDownload' && !confirmed) {
    body = (
      <div>
        <h3>1. Open the address on your iPhone</h3>
        {status.address && (
          <div className="address-row">
            <div>
              <p className="address">{status.address}</p>
              <p className="muted">Open it in Safari. The QR code is only a shortcut for typing it.</p>
            </div>
            <QrCode text={status.address} />
          </div>
        )}
        <h3>2. Download the certificate</h3>
        <p>
          Tap "Download Certificate". In Settings, under "Profile Downloaded", open the certificate details
          before installing and find its SHA-256 fingerprint. Compare it with this one:
        </p>
        {status.caFingerprint ? (
          <Fingerprint hex={status.caFingerprint} />
        ) : (
          <p className="muted">Waiting for the fingerprint…</p>
        )}
        <p><strong>Does this match your iPhone?</strong></p>
        <div className="row-actions">
          <button className="btn btn-primary" disabled={!status.caFingerprint} onClick={() => setConfirmed(true)}>
            Yes
          </button>
          <button className="btn btn-danger" onClick={() => void decline()}>No</button>
        </div>
      </div>
    );
  } else if (status.state === 'caDownload') {
    body = (
      <div>
        <h3>Trust the certificate and install the app</h3>
        <ol>
          <li>On the iPhone, install the profile, then enable full trust under Settings → General → About → Certificate Trust Settings.</li>
          <li>Back in Safari, tap "Continue". The page opens over HTTPS. Use Share → Add to Home Screen.</li>
          <li>Open the app from the new home-screen icon. The pairing screen appears.</li>
        </ol>
        <button className="btn btn-primary" disabled={busy} onClick={() => void act(api.showCode)}>
          Show Pairing Code
        </button>
      </div>
    );
  } else {
    body = <p className="warning" role="alert">The pairing window was closed. Close this dialog and start again.</p>;
  }

  return (
    <>
      {body}
      {lost && <p className="muted" role="status">Lost contact with the host. Retrying…</p>}
      {actionError && <p className="field-error" role="alert">The request failed. Try again.</p>}
    </>
  );
}

type Stage = 'choose' | 'starting' | 'active' | 'declined';

export function AddDeviceWizard({ templates, onClose }: { templates: DeviceTemplate[] | null; onClose: () => void }) {
  const [stage, setStage] = useState<Stage>('choose');
  const [initial, setInitial] = useState<PairingStatus | null>(null);
  const [startError, setStartError] = useState(false);
  const [done, setDone] = useState(false);

  const models = (templates ?? []).filter((t) => !t.generic);

  const start = async (templateId?: string) => {
    setStage('starting');
    setStartError(false);
    try {
      setInitial(await api.startPairing(templateId));
      setStage('active');
    } catch {
      setStartError(true);
      setStage('choose');
    }
  };

  // Closing at any step cancels pairing, unless it already finished or was declined (cancel was sent then).
  const close = () => {
    if ((stage === 'active' || stage === 'starting') && !done) {
      void api.cancelPairing().catch(() => undefined);
    }
    onClose();
  };

  return (
    <div className="overlay">
      <div className="dialog" role="dialog" aria-modal="true" aria-label="Add Device">
        <div className="section-head">
          <h2>Add Device</h2>
          <button className="btn btn-quiet" onClick={close}>Close</button>
        </div>
        {stage === 'choose' && (
          <div>
            <h3>Choose your iPhone model</h3>
            {startError && <p className="field-error" role="alert">Could not start pairing. Try again.</p>}
            {templates === null && <p className="muted">Loading models…</p>}
            <ul className="model-list">
              {models.map((t) => (
                <li key={t.id}>
                  <button className="btn model-btn" onClick={() => void start(t.id)}>{t.name}</button>
                </li>
              ))}
              <li>
                <button className="btn model-btn" onClick={() => void start()}>My device isn't listed</button>
              </li>
            </ul>
          </div>
        )}
        {stage === 'starting' && <p className="muted">Opening the pairing window…</p>}
        {stage === 'active' && initial && (
          <PairingFlow
            initial={initial}
            onDecline={() => setStage('declined')}
            onDone={() => {
              setDone(true);
              onClose();
            }}
          />
        )}
        {stage === 'declined' && (
          <div>
            <p className="warning" role="alert">
              Pairing was cancelled because someone may be intercepting the connection. Check your network and try again.
            </p>
            <button className="btn" onClick={onClose}>Close</button>
          </div>
        )}
      </div>
    </div>
  );
}
