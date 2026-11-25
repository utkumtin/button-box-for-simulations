import type { DeviceTemplate, Grid, GridCell, Layout, Profile, Widget } from '@bbs/protocol';
import { fits, minimumSpan, type Span, type WidgetType } from '@bbs/widgets';
import { behaviorLockViolation } from './behavior';
import { newWidgetId } from './widgetIds';

/**
 * Pure edits on a Profile (decisions §6, §11). Every function returns a new object and never mutates its input.
 * A refused edit returns `{ ok: false, reason }` with a sentence for the user.
 *
 * Rules enforced while editing: grid 1-24 columns x 1-16 rows; a widget stays inside the grid and at least its minimum
 * physical size (`@bbs/widgets` minSize); widgets in one layout never overlap, because their touch areas would overlap
 * on the phone (Phase 4 rule; the host checks it again on save and import).
 */

export const MIN_COLUMNS = 1;
export const MAX_COLUMNS = 24;
export const MIN_ROWS = 1;
export const MAX_ROWS = 16;
export const DEFAULT_GRID: Grid = { columns: 8, rows: 3 };
/** Schema limits (profile: layouts, layout: widgets). */
export const MAX_LAYOUTS = 16;
export const MAX_WIDGETS = 256;
export const MAX_PROFILE_NAME = 64;

export type OpResult<Extra = object> = ({ ok: true; profile: Profile } & Extra) | { ok: false; reason: string };

const refuse = (reason: string): { ok: false; reason: string } => ({ ok: false, reason });

export const WIDGET_TYPE_NAMES: Record<WidgetType, string> = {
  toggle: 'Toggle',
  encoder: 'Encoder',
  switch: 'Switch',
  dualAction: 'Dual-action button',
};

/** `Toggle "Beacon"` or `Toggle toggle-1`, for messages. */
export function describeWidget(widget: Widget): string {
  return `${WIDGET_TYPE_NAMES[widget.type]} ${widget.label ? `"${widget.label}"` : widget.id}`;
}

/** True when the text has a control character (U+0000-U+001F, U+007F), which the schema forbids in names and labels. */
export function hasControlChar(value: string): boolean {
  return [...value].some((ch) => {
    const code = ch.charCodeAt(0);
    return code <= 0x1f || code === 0x7f;
  });
}

// ---------------------------------------------------------------- geometry

export function cellsOverlap(a: GridCell, b: GridCell): boolean {
  return a.col < b.col + b.colSpan && b.col < a.col + a.colSpan && a.row < b.row + b.rowSpan && b.row < a.row + a.rowSpan;
}

export function insideGrid(cell: GridCell, grid: Grid): boolean {
  return (
    Number.isInteger(cell.col) &&
    Number.isInteger(cell.row) &&
    Number.isInteger(cell.colSpan) &&
    Number.isInteger(cell.rowSpan) &&
    cell.col >= 0 &&
    cell.row >= 0 &&
    cell.colSpan >= 1 &&
    cell.rowSpan >= 1 &&
    cell.col + cell.colSpan <= grid.columns &&
    cell.row + cell.rowSpan <= grid.rows
  );
}

export function gridInLimits(grid: Grid): boolean {
  return (
    Number.isInteger(grid.columns) &&
    Number.isInteger(grid.rows) &&
    grid.columns >= MIN_COLUMNS &&
    grid.columns <= MAX_COLUMNS &&
    grid.rows >= MIN_ROWS &&
    grid.rows <= MAX_ROWS
  );
}

/** First free spot (row by row, left to right) for a block of this span, ignoring the widget `ignoreId`. */
export function findFreeCell(layout: Layout, span: Span, ignoreId?: string): GridCell | null {
  const others = layout.widgets.filter((w) => w.id !== ignoreId);
  for (let row = 0; row + span.rowSpan <= layout.grid.rows; row++) {
    for (let col = 0; col + span.colSpan <= layout.grid.columns; col++) {
      const candidate: GridCell = { col, row, colSpan: span.colSpan, rowSpan: span.rowSpan };
      if (!others.some((w) => cellsOverlap(w.cell, candidate))) {
        return candidate;
      }
    }
  }
  return null;
}

// ---------------------------------------------------------------- helpers

function findLayout(profile: Profile, templateId: string): Layout | undefined {
  return profile.layouts.find((l) => l.templateId === templateId);
}

function withLayout(profile: Profile, layout: Layout): Profile {
  return { ...profile, layouts: profile.layouts.map((l) => (l.templateId === layout.templateId ? layout : l)) };
}

/** Runs `change` on one layout; a string result is a refusal reason. */
function editLayout(profile: Profile, templateId: string, change: (layout: Layout) => Layout | string): OpResult {
  const layout = findLayout(profile, templateId);
  if (!layout) {
    return refuse('This profile has no layout for that device.');
  }
  const result = change(layout);
  return typeof result === 'string' ? refuse(result) : { ok: true, profile: withLayout(profile, result) };
}

function replaceWidget(layout: Layout, widget: Widget): Layout {
  return { ...layout, widgets: layout.widgets.map((w) => (w.id === widget.id ? widget : w)) };
}

/** Why `cell` is not allowed for `widget` in `layout`, or null. The size check is skipped when only moving. */
function placementProblem(
  layout: Layout,
  template: DeviceTemplate,
  widget: Widget,
  cell: GridCell,
  checkSize: boolean,
): string | null {
  if (!insideGrid(cell, layout.grid)) {
    return `${describeWidget(widget)} would stick out of the grid.`;
  }
  if (checkSize && !fits(widget.type, cell, layout.grid, template.screen)) {
    return `${describeWidget(widget)} would be too small to touch reliably on ${template.name}.`;
  }
  const other = layout.widgets.find((w) => w.id !== widget.id && cellsOverlap(w.cell, cell));
  if (other) {
    return `${describeWidget(widget)} would overlap ${describeWidget(other)}.`;
  }
  return null;
}

// ---------------------------------------------------------------- layouts

/** Adds an empty layout (8 x 3) for a template. */
export function addLayout(profile: Profile, template: DeviceTemplate): OpResult {
  if (findLayout(profile, template.id)) {
    return refuse(`This profile already has a layout for ${template.name}.`);
  }
  if (profile.layouts.length >= MAX_LAYOUTS) {
    return refuse(`A profile can hold at most ${MAX_LAYOUTS} layouts.`);
  }
  const layout: Layout = { schemaVersion: 1, templateId: template.id, grid: { ...DEFAULT_GRID }, widgets: [] };
  return { ok: true, profile: { ...profile, layouts: [...profile.layouts, layout] } };
}

export function removeLayout(profile: Profile, templateId: string): OpResult {
  if (!findLayout(profile, templateId)) {
    return refuse('This profile has no layout for that device.');
  }
  return { ok: true, profile: { ...profile, layouts: profile.layouts.filter((l) => l.templateId !== templateId) } };
}

export function setGrid(profile: Profile, template: DeviceTemplate, grid: Grid): OpResult {
  if (!gridInLimits(grid)) {
    return refuse(`The grid must have ${MIN_COLUMNS} to ${MAX_COLUMNS} columns and ${MIN_ROWS} to ${MAX_ROWS} rows.`);
  }
  return editLayout(profile, template.id, (layout) => {
    for (const w of layout.widgets) {
      if (!insideGrid(w.cell, grid)) {
        return `${describeWidget(w)} would fall outside a ${grid.columns} x ${grid.rows} grid.`;
      }
      if (!fits(w.type, w.cell, grid, template.screen)) {
        return `${describeWidget(w)} would be too small on a ${grid.columns} x ${grid.rows} grid.`;
      }
    }
    return { ...layout, grid: { columns: grid.columns, rows: grid.rows } };
  });
}

/**
 * Copies a layout to another template: same grid and cells, nothing resized. `belowMinimum` lists the widget ids that
 * are too small on the target; the editor flags them and saving stays refused until they are fixed (decisions §6).
 * An existing layout on the target is only replaced with `replace: true`.
 */
export function copyLayout(
  profile: Profile,
  fromTemplateId: string,
  target: DeviceTemplate,
  replace = false,
): OpResult<{ belowMinimum: string[] }> {
  const source = findLayout(profile, fromTemplateId);
  if (!source) {
    return refuse('This profile has no layout for that device.');
  }
  if (fromTemplateId === target.id) {
    return refuse('Choose a different device to copy to.');
  }
  const existing = findLayout(profile, target.id);
  if (existing && !replace) {
    return refuse(`This profile already has a layout for ${target.name}.`);
  }
  if (!existing && profile.layouts.length >= MAX_LAYOUTS) {
    return refuse(`A profile can hold at most ${MAX_LAYOUTS} layouts.`);
  }
  const copy: Layout = structuredClone({ ...source, templateId: target.id });
  const belowMinimum = copy.widgets.filter((w) => !fits(w.type, w.cell, copy.grid, target.screen)).map((w) => w.id);
  const layouts = existing
    ? profile.layouts.map((l) => (l.templateId === target.id ? copy : l))
    : [...profile.layouts, copy];
  return { ok: true, profile: { ...profile, layouts }, belowMinimum };
}

// ---------------------------------------------------------------- widgets

/** A widget without the parts the editor assigns (id, cell). */
export type WidgetDraft = Widget extends infer W ? (W extends Widget ? Omit<W, 'id' | 'cell'> : never) : never;

/** An empty widget of a type; the user still picks actions in the inspector (problems.ts flags it until then). */
export function blankWidget(type: WidgetType): WidgetDraft {
  switch (type) {
    case 'toggle':
      return { type, action: '' };
    case 'encoder':
      return { type, minus: '', plus: '' };
    case 'switch':
      return {
        type,
        positions: [
          { angle: -30, action: '' },
          { angle: 30, action: '' },
        ],
      };
    case 'dualAction':
      return { type, holdMode: 'fire' };
  }
}

/**
 * Places a widget at the first free spot, at its minimum span (or `span` where that is larger). The id comes from the
 * label or type. Refused when the layout is full or the minimum span does not fit the grid.
 */
export function addWidget(
  profile: Profile,
  template: DeviceTemplate,
  draft: WidgetDraft,
  span?: Span,
): OpResult<{ widgetId: string }> {
  const layout = findLayout(profile, template.id);
  if (!layout) {
    return refuse('This profile has no layout for that device.');
  }
  if (layout.widgets.length >= MAX_WIDGETS) {
    return refuse(`A layout can hold at most ${MAX_WIDGETS} widgets.`);
  }
  const min = minimumSpan(draft.type, layout.grid, template.screen);
  const wanted: Span = {
    colSpan: Math.max(min.colSpan, span?.colSpan ?? 1),
    rowSpan: Math.max(min.rowSpan, span?.rowSpan ?? 1),
  };
  const cell = findFreeCell(layout, wanted);
  if (!cell) {
    return refuse('There is no free space left for this widget. Remove a widget or make the grid larger.');
  }
  const widgetId = newWidgetId(
    layout.widgets.map((w) => w.id),
    draft.type,
    draft.label,
  );
  const widget = normalizeWidget({ ...structuredClone(draft), id: widgetId, cell } as Widget);
  return { ok: true, profile: withLayout(profile, { ...layout, widgets: [...layout.widgets, widget] }), widgetId };
}

/** Moves a widget; its size stays. A widget that is already too small can still be moved. */
export function moveWidget(
  profile: Profile,
  template: DeviceTemplate,
  widgetId: string,
  to: { col: number; row: number },
): OpResult {
  return editLayout(profile, template.id, (layout) => {
    const widget = layout.widgets.find((w) => w.id === widgetId);
    if (!widget) {
      return 'That widget no longer exists.';
    }
    const cell: GridCell = { ...widget.cell, col: to.col, row: to.row };
    return placementProblem(layout, template, widget, cell, false) ?? replaceWidget(layout, { ...widget, cell });
  });
}

/** Sets a widget's whole cell (so a drag on the left or top handle can change the origin too). */
export function resizeWidget(profile: Profile, template: DeviceTemplate, widgetId: string, cell: GridCell): OpResult {
  return editLayout(profile, template.id, (layout) => {
    const widget = layout.widgets.find((w) => w.id === widgetId);
    if (!widget) {
      return 'That widget no longer exists.';
    }
    const next: GridCell = { col: cell.col, row: cell.row, colSpan: cell.colSpan, rowSpan: cell.rowSpan };
    return placementProblem(layout, template, widget, next, true) ?? replaceWidget(layout, { ...widget, cell: next });
  });
}

export function deleteWidget(profile: Profile, templateId: string, widgetId: string): OpResult {
  return editLayout(profile, templateId, (layout) =>
    layout.widgets.some((w) => w.id === widgetId)
      ? { ...layout, widgets: layout.widgets.filter((w) => w.id !== widgetId) }
      : 'That widget no longer exists.',
  );
}

/** A copy of the widget (new id) at the first free spot of the same span. */
export function duplicateWidget(
  profile: Profile,
  template: DeviceTemplate,
  widgetId: string,
): OpResult<{ widgetId: string }> {
  const layout = findLayout(profile, template.id);
  const widget = layout?.widgets.find((w) => w.id === widgetId);
  if (!layout || !widget) {
    return refuse('That widget no longer exists.');
  }
  if (layout.widgets.length >= MAX_WIDGETS) {
    return refuse(`A layout can hold at most ${MAX_WIDGETS} widgets.`);
  }
  const cell = findFreeCell(layout, widget.cell);
  if (!cell) {
    return refuse('There is no free space left for a copy. Remove a widget or make the grid larger.');
  }
  const id = newWidgetId(
    layout.widgets.map((w) => w.id),
    widget.type,
    widget.label,
  );
  const copy: Widget = { ...structuredClone(widget), id, cell };
  return { ok: true, profile: withLayout(profile, { ...layout, widgets: [...layout.widgets, copy] }), widgetId: id };
}

/**
 * Changes a widget's properties. `change` receives a copy of the widget and returns the new one; id, type and cell are
 * always kept (use move and resize for the cell). Empty optional strings are removed, never written as "".
 * A widget with a behaviour keeps its behaviour, actions and position count (behavior.ts); other edits go through.
 */
export function updateWidget(
  profile: Profile,
  templateId: string,
  widgetId: string,
  change: (widget: Widget) => Widget,
): OpResult {
  return editLayout(profile, templateId, (layout) => {
    const widget = layout.widgets.find((w) => w.id === widgetId);
    if (!widget) {
      return 'That widget no longer exists.';
    }
    const changed = change(structuredClone(widget));
    const kept = { ...changed, id: widget.id, type: widget.type, cell: widget.cell } as Widget;
    const locked = behaviorLockViolation(widget, kept);
    if (locked) {
      return locked;
    }
    return replaceWidget(layout, normalizeWidget(kept));
  });
}

// ---------------------------------------------------------------- profile

/** Renames the profile. The name is trimmed; empty, over-long or control-character names are refused. */
export function renameProfile(profile: Profile, name: string): OpResult {
  const trimmed = name.trim();
  if (trimmed === '') {
    return refuse('The profile needs a name.');
  }
  if ([...trimmed].length > MAX_PROFILE_NAME) { // code points, like the schema and the host
    return refuse(`The name can have at most ${MAX_PROFILE_NAME} characters.`);
  }
  if (hasControlChar(trimmed)) {
    return refuse('The name cannot contain control characters.');
  }
  return { ok: true, profile: { ...profile, name: trimmed } };
}

// ---------------------------------------------------------------- normalizing

/** undefined for a missing, empty or blank string: the schema gives these strings minLength 1, so they must not exist. */
function text(value: string | undefined): string | undefined {
  return value === undefined || value.trim() === '' ? undefined : value;
}

function defined<T extends object>(obj: T): T {
  return Object.fromEntries(Object.entries(obj).filter(([, v]) => v !== undefined)) as T;
}

function normalizeLook<L extends { mode: string } | undefined>(look: L): L {
  if (!look) {
    return undefined as L;
  }
  const l = look as unknown as { mode: string; icon?: string; image?: string; on?: string };
  if (l.mode === 'icon') {
    const icon = text(l.icon);
    return (icon ? { mode: 'icon', icon } : undefined) as L;
  }
  const image = text(l.image);
  return (image ? defined({ mode: 'images', image, on: text(l.on) }) : undefined) as L;
}

function normalizeLayer<L extends { asset: string } | undefined>(layer: L): L {
  return (layer && text(layer.asset) ? layer : undefined) as L;
}

/**
 * Removes every empty optional string and every object left empty by that: label, position label/icon/action,
 * look icon/images, state fields, cycle, tap/hold, rotary layers. Required strings (toggle action, encoder minus/plus)
 * are kept as they are; problems.ts flags them while they are empty.
 */
export function normalizeWidget(widget: Widget): Widget {
  const state = widget.state ? defined({ lit: text(widget.state.lit), led: text(widget.state.led) }) : undefined;
  const base = {
    id: widget.id,
    cell: widget.cell,
    label: text(widget.label),
    longPressGuard: widget.longPressGuard,
    state: state && Object.keys(state).length > 0 ? state : undefined,
  };
  switch (widget.type) {
    case 'toggle':
      return defined({ ...base, type: widget.type, action: widget.action, look: normalizeLook(widget.look) }) as Widget;
    case 'encoder':
      return defined({ ...base, type: widget.type, minus: widget.minus, plus: widget.plus }) as Widget;
    case 'dualAction':
      return defined({
        ...base,
        type: widget.type,
        tap: text(widget.tap),
        hold: text(widget.hold),
        holdMode: widget.holdMode,
        behavior: widget.behavior,
        look: normalizeLook(widget.look),
      }) as Widget;
    case 'switch': {
      const layers = widget.rotary
        ? defined({
            base: normalizeLayer(widget.rotary.base),
            knob: normalizeLayer(widget.rotary.knob),
            highlight: normalizeLayer(widget.rotary.highlight),
          })
        : undefined;
      return defined({
        ...base,
        type: widget.type,
        positions: widget.positions.map((p) =>
          defined({ label: text(p.label), angle: p.angle, action: text(p.action), icon: text(p.icon) }),
        ),
        cycle: text(widget.cycle),
        behavior: widget.behavior,
        rotary: layers && Object.keys(layers).length > 0 ? layers : undefined,
      }) as Widget;
    }
  }
}
