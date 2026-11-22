import { useState, type FormEvent } from 'react';
import type { StoredDevice } from './lib/device';
import { readSafeArea } from './lib/mode';
import { pair } from './lib/pairing';
import { buildScreenInfo } from './lib/screen';

export function Pairing({
  notice,
  onPaired,
}: {
  notice: string | null;
  onPaired: (device: StoredDevice) => void;
}) {
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (busy || code.length !== 6) return;
    setBusy(true);
    setError(null);
    const screen = buildScreenInfo({
      innerWidth: window.innerWidth,
      innerHeight: window.innerHeight,
      safeArea: readSafeArea(),
      devicePixelRatio: window.devicePixelRatio,
    });
    const result = await pair({ code, screen });
    setBusy(false);
    if (result.ok) onPaired(result.response);
    else setError(result.message);
  };

  return (
    <main className="screen">
      <h1>Pair this phone</h1>
      {notice && <p className="note">{notice}</p>}
      <p>Enter the 6-digit code shown on the PC.</p>
      <form onSubmit={(e) => void submit(e)}>
        <input
          className="code"
          type="text"
          inputMode="numeric"
          autoComplete="one-time-code"
          pattern="[0-9]*"
          maxLength={6}
          value={code}
          aria-label="Pairing code"
          onChange={(e) => setCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
        />
        <button type="submit" className="primary" disabled={busy || code.length !== 6}>
          {busy ? 'Pairing…' : 'Pair'}
        </button>
      </form>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </main>
  );
}
