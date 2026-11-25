import type { Widget } from '@bbs/protocol';

/**
 * Which state preview controls a widget gets (decisions §5.2, §11). A widget with a `state` binding previews what it
 * binds; an engine start-stop widget has no binding but its lit (electric) and LED come from the behaviour, so it
 * always previews both. A switch also previews its positions.
 */
export interface PreviewControls {
  lit: boolean;
  led: boolean;
  position: boolean;
}

export function previewControls(widget: Widget): PreviewControls {
  const engine = widget.type === 'dualAction' && widget.behavior === 'engineStartStop';
  return {
    lit: engine || widget.state?.lit !== undefined,
    led: engine || widget.state?.led !== undefined,
    position: widget.type === 'switch',
  };
}

/** Label of the lit checkbox: "Electric" for the engine button, "Lit" otherwise. */
export function litLabel(widget: Widget): string {
  return widget.type === 'dualAction' && widget.behavior === 'engineStartStop' ? 'Electric on' : 'Lit';
}
