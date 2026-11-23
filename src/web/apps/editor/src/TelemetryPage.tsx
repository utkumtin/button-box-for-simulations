import { useState } from 'react';
import type { TelemetryView } from '@bbs/protocol';
import { api } from './api';
import { telemetryRows } from './lib/telemetryTable';
import { telemetryStatus } from './lib/topBar';
import { usePoll } from './usePoll';
import './pages.css';

/** Live telemetry fields, polled about 4 times per second (decisions §11). */
export function TelemetryPage() {
  const [view, setView] = useState<TelemetryView | null>(null);
  const [failed, setFailed] = useState(false);
  const [filter, setFilter] = useState('');

  usePoll(
    api.telemetry,
    (v) => {
      setView(v);
      setFailed(false);
    },
    () => setFailed(true),
    250,
  );

  const status = telemetryStatus(view);
  const rows = view ? telemetryRows(view.fields, filter) : [];

  return (
    <section>
      <h2>Telemetry</h2>
      <p>
        <span className={status.live ? 'dot dot-on telemetry-dot' : 'dot dot-off telemetry-dot'} aria-hidden="true" />
        {status.label}
      </p>
      {failed && <p className="field-error" role="alert">Cannot read telemetry from the host.</p>}
      <label className="telemetry-filter">
        <span className="muted">Filter</span>
        <input
          className="input"
          type="search"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder="Field name"
        />
      </label>
      {rows.length === 0 ? (
        <p className="muted">No telemetry fields.</p>
      ) : (
        <table className="telemetry-table">
          <thead>
            <tr><th scope="col">Name</th><th scope="col">Value</th></tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.name}><td>{r.name}</td><td className="telemetry-value">{r.value}</td></tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
