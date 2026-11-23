import type { Cutout, Grid, GridCell, ScreenInfo } from '@bbs/protocol';

/**
 * Pure geometry for the editor canvas (decisions §11): the device frame scaled to the available width, the safe
 * area, the cutout, and the snapping of pointer movement to grid cells. All pixel values are CSS px.
 */

export interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface Area {
  width: number;
  height: number;
}

export interface FrameScale {
  /** CSS px per screen point. */
  scale: number;
  width: number;
  height: number;
}

/** A frame is never drawn larger than this many px per point, so a wide window does not blow the preview up. */
export const MAX_FRAME_SCALE = 1.5;

/** The screen drawn at the available width; the aspect ratio of the screen is kept. */
export function frameScale(
  screen: Pick<ScreenInfo, 'width' | 'height'>,
  availableWidth: number,
  maxScale = MAX_FRAME_SCALE,
): FrameScale {
  const scale = availableWidth > 0 && screen.width > 0 ? Math.min(availableWidth / screen.width, maxScale) : 0;
  return { scale, width: screen.width * scale, height: screen.height * scale };
}

/** The usable area inside the safe-area insets, in frame px. */
export function safeAreaBox(screen: ScreenInfo, scale: number): Box {
  const s = screen.safeArea;
  return {
    x: s.left * scale,
    y: s.top * scale,
    width: (screen.width - s.left - s.right) * scale,
    height: (screen.height - s.top - s.bottom) * scale,
  };
}

// Landscape, on the left edge (the phone is held with the camera side to the left). Sizes are those of an iPhone 14 Pro
// island and an iPhone X notch; the picture only shows where the cutout is, it is not a measurement.
const ISLAND = { width: 37, height: 124, inset: 11 };
const NOTCH = { width: 30, height: 209 };

/** The cutout drawn on the left edge, or null when there is none or its shape is not known. */
export function cutoutBox(cutout: Cutout, screen: Pick<ScreenInfo, 'width' | 'height'>, scale: number): Box | null {
  switch (cutout) {
    case 'dynamicIsland':
      return {
        x: ISLAND.inset * scale,
        y: ((screen.height - ISLAND.height) / 2) * scale,
        width: ISLAND.width * scale,
        height: ISLAND.height * scale,
      };
    case 'notch':
      return { x: 0, y: ((screen.height - NOTCH.height) / 2) * scale, width: NOTCH.width * scale, height: NOTCH.height * scale };
    default:
      return null;
  }
}

export function cellPixels(grid: Grid, area: Area): Area {
  return { width: area.width / grid.columns, height: area.height / grid.rows };
}

/** Pixel box of a cell span inside the area. */
export function cellBox(cell: GridCell, grid: Grid, area: Area): Box {
  const c = cellPixels(grid, area);
  return { x: cell.col * c.width, y: cell.row * c.height, width: cell.colSpan * c.width, height: cell.rowSpan * c.height };
}

const clamp = (value: number, min: number, max: number): number => Math.min(Math.max(value, min), max);

/**
 * Whole cells a pointer movement is worth: the nearest cell, so a widget jumps when it is dragged past the middle of
 * the next cell (a movement of exactly half a cell rounds up). `|| 0` turns -0 into 0.
 */
export function snapDelta(deltaPx: number, cellPx: number): number {
  return cellPx > 0 ? Math.round(deltaPx / cellPx) || 0 : 0;
}

/** Where a dragged widget lands: its size stays, the origin follows the pointer and stays inside the grid. */
export function moveTarget(cell: GridCell, dxPx: number, dyPx: number, grid: Grid, area: Area): GridCell {
  const c = cellPixels(grid, area);
  return {
    ...cell,
    col: clamp(cell.col + snapDelta(dxPx, c.width), 0, Math.max(0, grid.columns - cell.colSpan)),
    row: clamp(cell.row + snapDelta(dyPx, c.height), 0, Math.max(0, grid.rows - cell.rowSpan)),
  };
}

export type Handle = 'n' | 's' | 'e' | 'w' | 'ne' | 'nw' | 'se' | 'sw';

export const HANDLES: readonly Handle[] = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'];

/**
 * The cell after dragging a handle: the edge(s) the handle sits on follow the pointer, the opposite edges stay.
 * The result is at least one cell wide and high and inside the grid. (Minimum physical size is checked by
 * `resizeWidget`, not here.)
 */
export function resizeTarget(cell: GridCell, handle: Handle, dxPx: number, dyPx: number, grid: Grid, area: Area): GridCell {
  const c = cellPixels(grid, area);
  const dCol = snapDelta(dxPx, c.width);
  const dRow = snapDelta(dyPx, c.height);
  let left = cell.col;
  let top = cell.row;
  let right = cell.col + cell.colSpan;
  let bottom = cell.row + cell.rowSpan;
  if (handle.includes('w')) {
    left = clamp(left + dCol, 0, right - 1);
  }
  if (handle.includes('e')) {
    right = clamp(right + dCol, left + 1, grid.columns);
  }
  if (handle.includes('n')) {
    top = clamp(top + dRow, 0, bottom - 1);
  }
  if (handle.includes('s')) {
    bottom = clamp(bottom + dRow, top + 1, grid.rows);
  }
  return { col: left, row: top, colSpan: right - left, rowSpan: bottom - top };
}

export function sameCell(a: GridCell, b: GridCell): boolean {
  return a.col === b.col && a.row === b.row && a.colSpan === b.colSpan && a.rowSpan === b.rowSpan;
}
