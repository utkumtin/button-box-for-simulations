import type { Catalog, DeviceTemplate, Layout, Profile, Widget } from '@bbs/protocol';
import { fits } from '@bbs/widgets';
import { behaviorShapeProblem } from './behavior';
import {
  cellsOverlap,
  describeWidget,
  gridInLimits,
  hasControlChar,
  insideGrid,
  MAX_COLUMNS,
  MAX_ROWS,
} from './layoutOps';

/**
 * Everything that must be fixed before a profile can be saved (decisions §6, §11): save is allowed only when the
 * list is empty. The host checks the same rules again on save and import. Messages are plain English for the user.
 * Widgets in one layout must not overlap: their touch areas would overlap on the phone (Phase 4 rule).
 */
export interface Problem {
  widgetId?: string;
  message: string;
}

export interface ProfileProblem extends Problem {
  templateId: string;
}


const MAX_LABEL = 32;

/** The actions a widget can send, with a name for each slot. An empty slot has `id` undefined or "". */
function slots(widget: Widget): { what: string; id: string | undefined }[] {
  switch (widget.type) {
    case 'toggle':
      return [{ what: 'action', id: widget.action }];
    case 'encoder':
      return [
        { what: 'minus action', id: widget.minus },
        { what: 'plus action', id: widget.plus },
      ];
    case 'dualAction':
      return [
        { what: 'tap action', id: widget.tap },
        { what: 'hold action', id: widget.hold },
      ];
    case 'switch':
      return widget.positions.map((p, i) => ({ what: `position ${i + 1} action`, id: p.action }));
  }
}

function assetIds(widget: Widget): string[] {
  const ids: string[] = [];
  if ((widget.type === 'toggle' || widget.type === 'dualAction') && widget.look?.mode === 'images') {
    ids.push(widget.look.image);
    if (widget.look.on) {
      ids.push(widget.look.on);
    }
  }
  if (widget.type === 'switch' && widget.rotary) {
    for (const layer of [widget.rotary.base, widget.rotary.knob, widget.rotary.highlight]) {
      if (layer) {
        ids.push(layer.asset);
      }
    }
  }
  return ids;
}

// Lengths count code points like the schema and the host (an emoji is one character, not two UTF-16 units).
const badLabel = (label: string | undefined): boolean =>
  label !== undefined && ([...label].length > MAX_LABEL || hasControlChar(label));

export function layoutProblems(
  layout: Layout,
  template: DeviceTemplate,
  catalog: Catalog,
  assets?: ReadonlySet<string>,
): Problem[] {
  const problems: Problem[] = [];
  const add = (message: string, widget?: Widget) =>
    problems.push(widget ? { widgetId: widget.id, message } : { message });
  const known = new Set(catalog.actions.map((a) => a.id));

  const gridOk = gridInLimits(layout.grid);
  if (!gridOk) {
    add(`The grid must have 1 to ${MAX_COLUMNS} columns and 1 to ${MAX_ROWS} rows.`);
  }

  const seen = new Set<string>();
  for (const [index, w] of layout.widgets.entries()) {
    const name = describeWidget(w);

    if (seen.has(w.id)) {
      add(`${name} has the same id as another widget.`, w);
    }
    seen.add(w.id);

    if (gridOk) {
      if (!insideGrid(w.cell, layout.grid)) {
        add(`${name} is outside the grid.`, w);
      } else if (!fits(w.type, w.cell, layout.grid, template.screen)) {
        add(`${name} is too small to touch reliably on ${template.name}. Make it larger.`, w);
      }
      for (const other of layout.widgets.slice(0, index)) {
        if (cellsOverlap(w.cell, other.cell)) {
          add(`${name} overlaps ${describeWidget(other)}.`, w);
        }
      }
    }

    if (badLabel(w.label)) {
      add(`${name} has a label that is too long or contains control characters.`, w);
    }

    for (const { what, id } of slots(w)) {
      if (id) {
        if (!known.has(id)) {
          add(`${name} uses a ${what} that does not exist in the ${catalog.game.toUpperCase()} catalog.`, w);
        }
      } else if (w.type === 'toggle' || w.type === 'encoder') {
        add(`${name} has no ${what}.`, w);
      }
    }

    if (w.type === 'switch') {
      for (const [i, p] of w.positions.entries()) {
        if (!p.action) {
          add(`${name}: position ${i + 1} has no action.`, w);
        }
        if (badLabel(p.label)) {
          add(`${name}: position ${i + 1} has a label that is too long or contains control characters.`, w);
        }
      }
      if (w.cycle !== undefined) {
        add(`${name} uses press-count switching, which is not available yet.`, w);
      }
    }

    if (w.type === 'dualAction') {
      if (!w.tap && !w.hold) {
        add(`${name} needs a tap action, a hold action or both.`, w);
      }
      if (w.holdMode === 'starterTakeover') {
        add(`${name} uses starter takeover, which is not available yet.`, w);
      }
    }

    const shape = behaviorShapeProblem(w, catalog.game);
    if (shape) {
      add(`${name} ${shape}`, w);
    }

    if (assets) {
      for (const id of assetIds(w)) {
        if (!assets.has(id)) {
          add(`${name} uses an image that is not in the asset library.`, w);
        }
      }
    }
  }
  return problems;
}

/** All problems of all layouts; each message starts with the template it is about. */
export function profileProblems(
  profile: Profile,
  templates: readonly DeviceTemplate[],
  catalog: Catalog,
  assets?: ReadonlySet<string>,
): ProfileProblem[] {
  const out: ProfileProblem[] = [];
  for (const layout of profile.layouts) {
    const template = templates.find((t) => t.id === layout.templateId);
    if (!template) {
      out.push({
        templateId: layout.templateId,
        message: `A layout belongs to a device that is not known (${layout.templateId}).`,
      });
      continue;
    }
    for (const p of layoutProblems(layout, template, catalog, assets)) {
      out.push({ ...p, templateId: layout.templateId, message: `${template.name}: ${p.message}` });
    }
  }
  return out;
}
