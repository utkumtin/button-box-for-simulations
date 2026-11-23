import type { Grid, ScreenInfo, Widget } from '@bbs/protocol';

/**
 * The minimum physical widget size (decisions §6). The usable area is the template's landscape screen minus its safe-area
 * insets (CSS px = pt); a widget's touch area is its whole cell span. Toggle and dual-action need 44 × 44 pt; encoder and
 * switch need 88 × 44 pt because they split into left/right halves of at least 44 pt each.
 * The C# twin is `Core/Engine/WidgetSize.cs`; both are tested against `minSize.cases.json`.
 */
export const MIN_TOUCH_PT = 44;

// Cell sizes are divisions, so a span that is exactly 44 pt can come out a hair below it.
const EPSILON = 1e-9;

export type WidgetType = Widget['type'];

export interface Size {
  width: number;
  height: number;
}

export interface Span {
  colSpan: number;
  rowSpan: number;
}

export function minimumSize(type: WidgetType): Size {
  return type === 'encoder' || type === 'switch'
    ? { width: 2 * MIN_TOUCH_PT, height: MIN_TOUCH_PT }
    : { width: MIN_TOUCH_PT, height: MIN_TOUCH_PT };
}

export function usableArea(screen: ScreenInfo): Size {
  const s = screen.safeArea;
  return { width: screen.width - s.left - s.right, height: screen.height - s.top - s.bottom };
}

export function cellSize(grid: Grid, screen: ScreenInfo): Size {
  const area = usableArea(screen);
  return { width: area.width / grid.columns, height: area.height / grid.rows };
}

/** True when a widget of this type spanning these cells is at least the minimum size. */
export function fits(type: WidgetType, span: Span, grid: Grid, screen: ScreenInfo): boolean {
  const cell = cellSize(grid, screen);
  const min = minimumSize(type);
  return span.colSpan * cell.width >= min.width - EPSILON && span.rowSpan * cell.height >= min.height - EPSILON;
}

/** The smallest span that fits. It can exceed the grid when the usable area is very small; then nothing of this type fits. */
export function minimumSpan(type: WidgetType, grid: Grid, screen: ScreenInfo): Span {
  const cell = cellSize(grid, screen);
  const min = minimumSize(type);
  const span = (minimum: number, size: number) => Math.max(1, Math.ceil(minimum / size - EPSILON));
  return { colSpan: span(min.width, cell.width), rowSpan: span(min.height, cell.height) };
}
