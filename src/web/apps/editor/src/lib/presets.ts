import type { Catalog } from '@bbs/protocol';
import type { WidgetDraft } from './layoutOps';

/**
 * Presets (decisions §11, §5.1-5.3): ready-made widgets with their looks and the actions available today. They exist
 * only for ETS2 and ATS catalogs and return null when the catalog lacks one of the actions. Values come from the
 * Phase 3 demo profile. Images are used only when their asset ids are in the library (`assets`); otherwise the widget
 * has no image and is drawn in code. The editor does not offer `cycle` or `starterTakeover` before their phases.
 * Each returns a draft; `addWidget` assigns the id and the cell.
 */

const ROTARY_BASE = '02abf8a01862694028bb6eed91cde12ab712b3e24337ef0905cbe4bcc539bff9';
const ROTARY_KNOB = '8e5488f4fcf1e99596ef42ef7fbc103f4d3c2af8fa4fb7b30271a4cb05af0087';
const ENGINE_IMAGE = 'e744710295a932027d7feef53bfc06783e4f11eb5e89145f846ea07896c52572';

type SwitchDraft = Extract<WidgetDraft, { type: 'switch' }>;
type DualDraft = Extract<WidgetDraft, { type: 'dualAction' }>;

function available(catalog: Catalog, ids: string[]): boolean {
  if (catalog.game !== 'ets2' && catalog.game !== 'ats') {
    return false;
  }
  const have = new Set(catalog.actions.map((a) => a.id));
  return ids.every((id) => have.has(id));
}

function rotary(assets: ReadonlySet<string>): Pick<SwitchDraft, 'rotary'> {
  if (!assets.has(ROTARY_BASE) || !assets.has(ROTARY_KNOB)) {
    return {};
  }
  return { rotary: { base: { asset: ROTARY_BASE, scale: 1 }, knob: { asset: ROTARY_KNOB, scale: 0.55 } } };
}

/** OFF / AUTO / LOW. HIGH comes with the high-beam logic of Phase 5 (decisions §5.1). */
export function headlightPreset(catalog: Catalog, assets: ReadonlySet<string>): WidgetDraft | null {
  if (!available(catalog, ['light-off', 'light-park', 'light-on'])) {
    return null;
  }
  return {
    type: 'switch',
    label: 'LIGHTS',
    positions: [
      { label: 'OFF', angle: -30, action: 'light-off' },
      { label: 'AUTO', angle: 0, action: 'light-park' },
      { icon: 'car-light-dimmed', angle: 30, action: 'light-on' },
    ],
    ...rotary(assets),
  };
}

export function wiperPreset(catalog: Catalog, assets: ReadonlySet<string>): WidgetDraft | null {
  if (!available(catalog, ['wipers-off', 'wipers-auto', 'wipers-low', 'wipers-fast'])) {
    return null;
  }
  return {
    type: 'switch',
    label: 'WIPERS',
    positions: [
      { label: 'OFF', angle: -30, action: 'wipers-off' },
      { label: 'AUTO', angle: 0, action: 'wipers-auto' },
      { label: 'LOW', angle: 30, action: 'wipers-low' },
      { label: 'FAST', angle: 60, action: 'wipers-fast' },
    ],
    ...rotary(assets),
  };
}

/** Generic Phase 3 behaviour (tap ignition on, hold starter); the full §5.2 behaviour comes in Phase 5. */
export function engineButtonPreset(catalog: Catalog, assets: ReadonlySet<string>): WidgetDraft | null {
  if (!available(catalog, ['ignition-on', 'starter'])) {
    return null;
  }
  const draft: DualDraft = {
    type: 'dualAction',
    label: 'ENGINE START STOP',
    tap: 'ignition-on',
    hold: 'starter',
    holdMode: 'fire',
    state: { lit: 'truck.electric.enabled', led: 'truck.engine.enabled' },
  };
  if (assets.has(ENGINE_IMAGE)) {
    draft.look = { mode: 'images', image: ENGINE_IMAGE };
  }
  return draft;
}
