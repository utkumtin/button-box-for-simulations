import type { Layout, Rotary, RotaryLayer, SwitchPosition, SwitchWidget, Widget } from '@bbs/protocol';

/** Pure widget edits for the inspector and the rotary composer (decisions §5, §7, §11). */

export const MIN_POSITIONS = 2;
export const MAX_POSITIONS = 8;

export type LayerKey = 'base' | 'knob' | 'highlight';
export const LAYER_KEYS: readonly LayerKey[] = ['base', 'knob', 'highlight'];
export type LayerValue = 'scale' | 'offsetX' | 'offsetY';

export const SCALE_RANGE = { min: 0.1, max: 2 } as const;
export const OFFSET_RANGE = { min: -0.5, max: 0.5 } as const;
export const ANGLE_RANGE = { min: -180, max: 180 } as const;

/** Scale the renderer uses for a layer without one: the knob is drawn smaller than the base (presets use 0.55). */
export function defaultLayerScale(key: LayerKey): number {
  return key === 'knob' ? 0.55 : 1;
}

/** What the layer sliders show when the layer has no value of its own. */
export function layerValue(layer: RotaryLayer | undefined, key: LayerKey, prop: LayerValue): number {
  const own = layer?.[prop];
  if (own !== undefined) {
    return own;
  }
  return prop === 'scale' ? defaultLayerScale(key) : 0;
}

function withRotary(widget: SwitchWidget, rotary: Rotary): SwitchWidget {
  return { ...widget, rotary };
}

/** Picks the image of a layer; undefined removes the layer (that layer is then drawn in code). */
export function setLayerAsset(widget: SwitchWidget, key: LayerKey, assetId: string | undefined): SwitchWidget {
  const rotary: Rotary = { ...widget.rotary };
  if (assetId === undefined) {
    delete rotary[key];
  } else {
    const existing = rotary[key];
    rotary[key] = { ...existing, asset: assetId, scale: existing?.scale ?? defaultLayerScale(key) };
  }
  return withRotary(widget, rotary);
}

/** Sets scale or an offset of a layer that has an image. Without a layer nothing changes (there is nothing to calibrate). */
export function setLayerValue(widget: SwitchWidget, key: LayerKey, prop: LayerValue, value: number): SwitchWidget {
  const layer = widget.rotary?.[key];
  if (!layer) {
    return widget;
  }
  const range = prop === 'scale' ? SCALE_RANGE : OFFSET_RANGE;
  const clamped = Math.min(Math.max(value, range.min), range.max);
  return withRotary(widget, { ...widget.rotary, [key]: { ...layer, [prop]: clamped } });
}

/** A new last position, 30 degrees after the previous one (kept inside the allowed angles). Null at the limit. */
export function addPosition(widget: SwitchWidget): SwitchWidget | null {
  if (widget.positions.length >= MAX_POSITIONS) {
    return null;
  }
  const last = widget.positions[widget.positions.length - 1];
  const angle = Math.min((last?.angle ?? 0) + 30, ANGLE_RANGE.max);
  return { ...widget, positions: [...widget.positions, { angle, action: '' }] as SwitchWidget['positions'] };
}

/** Removes a position; a switch keeps at least two. Null when refused. */
export function removePosition(widget: SwitchWidget, index: number): SwitchWidget | null {
  if (widget.positions.length <= MIN_POSITIONS || index < 0 || index >= widget.positions.length) {
    return null;
  }
  return { ...widget, positions: widget.positions.filter((_, i) => i !== index) as SwitchWidget['positions'] };
}

export function updatePosition(
  widget: SwitchWidget,
  index: number,
  change: (position: SwitchPosition) => SwitchPosition,
): SwitchWidget {
  return {
    ...widget,
    positions: widget.positions.map((p, i) => (i === index ? change({ ...p }) : p)) as SwitchWidget['positions'],
  };
}

/** The action whose state field is offered as the default for `lit` and `led` (decisions §11). */
export function mainAction(widget: Widget): string | undefined {
  switch (widget.type) {
    case 'toggle':
      return widget.action || undefined;
    case 'dualAction':
      return widget.tap || widget.hold || undefined;
    case 'switch':
      return widget.positions.find((p) => p.action)?.action;
    case 'encoder':
      return undefined;
  }
}

/** The layout with one widget replaced by its not yet committed version (live slider preview). */
export function withLiveWidget(layout: Layout, live: Widget | null): Layout {
  if (!live || !layout.widgets.some((w) => w.id === live.id)) {
    return layout;
  }
  return { ...layout, widgets: layout.widgets.map((w) => (w.id === live.id ? live : w)) };
}
