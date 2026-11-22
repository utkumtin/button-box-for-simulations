import type { Layout, WidgetState } from '@bbs/protocol';
import type { GestureOut } from '@bbs/widgets';

/** What the phone knows about the host so far. Every host message replaces its part; nothing is merged. */
export interface HostModel {
  layout: Layout | null;
  /** True after `noLayout`; cleared by the next `layout`. */
  noLayout: boolean;
  states: Record<string, WidgetState>;
  /** Null until the first `status` message: telemetry is unknown, so nothing is dimmed or labelled. */
  status: { telemetry: boolean; gameListening: boolean } | null;
}

export const initialHostModel: HostModel = { layout: null, noLayout: false, states: {}, status: null };

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function isLayout(v: unknown): v is Layout {
  return isRecord(v) && isRecord(v.grid) && Array.isArray(v.widgets);
}

/**
 * Applies one raw host message (already JSON-parsed, untrusted). Unknown or malformed messages return the
 * same model object, so a bad frame can never blank the panel. `welcome` is handled by the connection.
 */
export function applyHostMessage(model: HostModel, msg: unknown): HostModel {
  if (!isRecord(msg)) return model;
  switch (msg.type) {
    case 'layout':
      return isLayout(msg.layout) ? { ...model, layout: msg.layout, noLayout: false } : model;
    case 'noLayout':
      return { ...model, layout: null, noLayout: true };
    case 'state':
      return isRecord(msg.widgets) ? { ...model, states: msg.widgets as Record<string, WidgetState> } : model;
    case 'status':
      return typeof msg.telemetry === 'boolean' && typeof msg.gameListening === 'boolean'
        ? { ...model, status: { telemetry: msg.telemetry, gameListening: msg.gameListening } }
        : model;
    default:
      return model;
  }
}

export type PanelLabel = 'No telemetry' | 'Game not listening' | null;

/** Dim and corner label (decisions §10). No telemetry wins over game not listening. */
export function panelNote(status: HostModel['status']): { dim: boolean; label: PanelLabel } {
  if (!status) return { dim: false, label: null };
  if (!status.telemetry) return { dim: true, label: 'No telemetry' };
  if (!status.gameListening) return { dim: true, label: 'Game not listening' };
  return { dim: false, label: null };
}

/**
 * The text to send for a finished gesture, or null when it must be dropped: presses are never queued,
 * and nothing is sent before the host accepted our hello (decisions §3, §10).
 */
export function gestureFrame(socketOpen: boolean, welcomed: boolean, g: GestureOut): string | null {
  return socketOpen && welcomed ? JSON.stringify(g) : null;
}
