import type { TelemetryFieldValue } from '@bbs/protocol';

export interface TelemetryRow {
  name: string;
  value: string;
}

/** Fields sorted by name (case-insensitive) and filtered by a case-insensitive substring of the name. */
export function telemetryRows(fields: readonly TelemetryFieldValue[], filter: string): TelemetryRow[] {
  const needle = filter.trim().toLowerCase();
  return fields
    .filter((f) => f.name.toLowerCase().includes(needle))
    .map((f) => ({ name: f.name, value: String(Math.trunc(f.value)) }))
    .sort((a, b) => {
      const x = a.name.toLowerCase();
      const y = b.name.toLowerCase();
      if (x !== y) return x < y ? -1 : 1;
      return a.name < b.name ? -1 : a.name > b.name ? 1 : 0;
    });
}
