import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { clearDevice, loadDevice, saveDevice, type StoredDevice } from './lib/device';
import { isStandalone } from './lib/mode';
import { isPortrait } from './lib/screen';
import { ButtonGrid } from '@bbs/widgets';
import { panelNote } from './lib/hostModel';
import { useConnection } from './lib/useConnection';
import { Pairing } from './Pairing';

const assetUrl = (assetId: string) => `/assets/${assetId}`;

function useViewport() {
  const [size, setSize] = useState(() => ({ w: window.innerWidth, h: window.innerHeight }));
  useEffect(() => {
    const update = () => setSize({ w: window.innerWidth, h: window.innerHeight });
    window.addEventListener('resize', update);
    window.addEventListener('orientationchange', update);
    return () => {
      window.removeEventListener('resize', update);
      window.removeEventListener('orientationchange', update);
    };
  }, []);
  return size;
}

function Message({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <main className="screen">
      <h1>{title}</h1>
      {children}
    </main>
  );
}

function Instructions() {
  return (
    <Message title="Add Button Box to your Home Screen">
      <ol className="steps">
        <li>Tap the Share button in Safari.</li>
        <li>Choose Add to Home Screen.</li>
        <li>Open Button Box from the Home Screen.</li>
      </ol>
    </Message>
  );
}

function Session({
  device,
  onForget,
  onReconnect,
}: {
  device: StoredDevice;
  onForget: () => void;
  onReconnect: () => void;
}) {
  const { status, host, sendGesture } = useConnection(device.token, onForget);
  if (status === 'replaced') {
    return (
      <Message title="Connected in another window">
        <button type="button" className="primary" onClick={onReconnect}>
          Reconnect
        </button>
      </Message>
    );
  }
  const connected = status === 'connected';
  const overlay = !connected && (
    <div className="overlay" role="status">
      {status === 'reconnecting' ? 'Reconnecting…' : 'Connecting…'}
    </div>
  );
  // A layout received earlier in this session stays on screen (disabled) while reconnecting.
  if (host.layout) {
    const { dim, label } = panelNote(host.status);
    return (
      <>
        <div className="panel">
          <div className="panel-grid">
            <ButtonGrid
              layout={host.layout}
              states={host.states}
              dim={dim}
              enabled={connected}
              onGesture={sendGesture}
              assetUrl={assetUrl}
            />
          </div>
          {label && <div className="panel-note">{label}</div>}
        </div>
        {overlay}
      </>
    );
  }
  if (connected && host.noLayout) {
    return <Message title="No layout for this device in the active profile." />;
  }
  return (
    <>
      <Message title={connected ? 'Connected' : 'Connecting…'} />
      {overlay}
    </>
  );
}

export function App() {
  const [standalone] = useState(isStandalone);
  const [device, setDevice] = useState<StoredDevice | null>(loadDevice);
  const [notice, setNotice] = useState<string | null>(null);
  const [session, setSession] = useState(0);
  const { w, h } = useViewport();

  const forget = useCallback(() => {
    clearDevice();
    setDevice(null);
    setNotice('This device was removed on the PC. Pair it again.');
  }, []);
  const reconnect = useCallback(() => setSession((s) => s + 1), []);

  if (!standalone) return <Instructions />;
  if (isPortrait(w, h)) {
    return <Message title="Rotate your phone to landscape to use the button box" />;
  }
  if (!device) {
    return (
      <Pairing
        notice={notice}
        onPaired={(d) => {
          saveDevice(d);
          setNotice(null);
          setDevice(d);
        }}
      />
    );
  }
  return <Session key={session} device={device} onForget={forget} onReconnect={reconnect} />;
}
