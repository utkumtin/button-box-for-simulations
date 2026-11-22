import type { Gesture, Layout, WidgetState } from '@bbs/protocol';

/**
 * The public contract of @bbs/widgets (decisions §2, §5): layout and state in, finished gestures out.
 * The package never talks to the network, so the editor can reuse it for its preview.
 */

/** A finished gesture, shaped like the client messages the phone sends (decisions §3). */
export type GestureOut =
  | { type: 'gesture'; widgetId: string; gesture: Gesture }
  | { type: 'select'; widgetId: string; position: number };

/** Touch constants (decisions §5, approved 2026-11-22). */
export const TOUCH = {
  /** Finger movement in CSS px beyond which a touch becomes a drag. */
  dragThresholdPx: 10,
  /** Hold time and long-press guard time in ms. */
  holdMs: 500,
  /** Switch drag: inside this fraction of the knob radius the finger angle is ignored. */
  switchDeadZone: 0.2,
} as const;

export interface ButtonGridProps {
  layout: Layout;
  /** Latest state per widget ID from the host. A missing entry means "no state yet". */
  states: Readonly<Record<string, WidgetState>>;
  /** No telemetry or game not listening: widgets with a state binding look dim and neutral. */
  dim: boolean;
  /** False ignores every touch (for example while reconnecting). */
  enabled: boolean;
  onGesture: (gesture: GestureOut) => void;
  /** Maps an asset ID (SHA-256 hex) to a URL. The phone uses `/assets/<id>`. */
  assetUrl: (assetId: string) => string;
}
