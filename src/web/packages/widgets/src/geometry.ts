import type { GridCell, Rotary, RotaryLayer } from '@bbs/protocol';

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/** Pixel rectangle of a grid cell span inside a grid of the given pixel size. */
export function cellRect(cell: GridCell, columns: number, rows: number, width: number, height: number): Rect {
  const cw = width / columns;
  const ch = height / rows;
  return { x: cell.col * cw, y: cell.row * ch, w: cell.colSpan * cw, h: cell.rowSpan * ch };
}

/** Size of the largest centred square of a widget rectangle. */
export function squareSide(w: number, h: number): number {
  return Math.min(w, h);
}

/** Scale of the code-drawn knob (fraction of the square) when no knob layer is given. */
export const CODE_KNOB_SCALE = 0.55;

export interface LayerBox {
  /** Top-left corner and size relative to the widget's centred square, in px. */
  left: number;
  top: number;
  size: number;
}

/** Layer calibration: scale is a fraction of the square, offsets are fractions of the square. */
export function layerBox(side: number, layer: Pick<RotaryLayer, 'scale' | 'offsetX' | 'offsetY'> | undefined, fallbackScale = 1): LayerBox {
  const size = side * (layer?.scale ?? fallbackScale);
  const cx = side / 2 + (layer?.offsetX ?? 0) * side;
  const cy = side / 2 + (layer?.offsetY ?? 0) * side;
  return { left: cx - size / 2, top: cy - size / 2, size };
}

/** Knob centre and radius relative to the widget rectangle's top-left, in px. */
export function knobGeometry(w: number, h: number, rotary: Rotary | undefined): { cx: number; cy: number; radius: number } {
  const side = squareSide(w, h);
  const box = layerBox(side, rotary?.knob, CODE_KNOB_SCALE);
  const ox = (w - side) / 2;
  const oy = (h - side) / 2;
  return { cx: ox + box.left + box.size / 2, cy: oy + box.top + box.size / 2, radius: box.size / 2 };
}

/** Angle in degrees, 0 = 12 o'clock, clockwise positive, range [0, 360). */
export function angleOf(dx: number, dy: number): number {
  const deg = (Math.atan2(dx, -dy) * 180) / Math.PI;
  return ((deg % 360) + 360) % 360;
}

/** Index of the position whose angle is closest (circularly) to the given angle. */
export function nearestPosition(angles: readonly number[], angle: number): number {
  let best = 0;
  let bestDiff = Infinity;
  angles.forEach((a, i) => {
    const raw = Math.abs((((a - angle) % 360) + 360) % 360);
    const diff = Math.min(raw, 360 - raw);
    if (diff < bestDiff) {
      bestDiff = diff;
      best = i;
    }
  });
  return best;
}
