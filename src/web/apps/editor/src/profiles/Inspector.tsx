import { useState } from 'react';
import type {
  AssetInfo,
  Catalog,
  DualActionWidget,
  EncoderWidget,
  Look,
  StateBinding,
  SwitchWidget,
  ToggleWidget,
  Widget,
} from '@bbs/protocol';
import { ICON_KEYS } from '@bbs/widgets';
import { behaviorLine, behaviorOf } from '../lib/behavior';
import { defaultStateField } from '../lib/catalogView';
import { WIDGET_TYPE_NAMES } from '../lib/layoutOps';
import { ActionSelect, CommitNumber, CommitText } from './fields';
import {
  ANGLE_RANGE,
  MAX_POSITIONS,
  MIN_POSITIONS,
  addPosition,
  mainAction,
  removePosition,
  updatePosition,
} from './inspectorOps';
import { AssetPicker, RotaryComposer } from './RotaryComposer';

const STATE_FIELDS_LIST = 'pe-state-fields';

export interface InspectorProps {
  widget: Widget;
  /** The not yet committed version shown while a composer slider moves, or null. */
  live: Widget | null;
  catalog: Catalog;
  assets: readonly AssetInfo[];
  onChange: (change: (w: Widget) => Widget) => void;
  onLive: (change: ((w: Widget) => Widget) | null) => void;
  onDuplicate: () => void;
  onDelete: () => void;
}

type Edit<W extends Widget> = (change: (w: W) => W) => void;

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="pe-field">
      <span>{label}</span>
      {children}
    </label>
  );
}

function IconSelect({ value, onChange, label }: { value: string | undefined; onChange: (icon: string | undefined) => void; label: string }) {
  return (
    <select className="input" aria-label={label} value={value ?? ''} onChange={(e) => onChange(e.target.value || undefined)}>
      <option value="">No icon</option>
      {value !== undefined && !ICON_KEYS.includes(value) && <option value={value}>Unknown icon ({value})</option>}
      {ICON_KEYS.map((k) => (
        <option key={k} value={k}>
          {k}
        </option>
      ))}
    </select>
  );
}

/** Look of a toggle or dual-action button: none, a built-in icon, or library images (off and optional on). */
function LookFields({ look, assets, onLook }: { look: Look | undefined; assets: readonly AssetInfo[]; onLook: (look: Look | undefined) => void }) {
  // "Images" without an image yet is not a look the widget can store, so the choice is kept here until one is picked.
  const [pendingImages, setPendingImages] = useState(false);
  const mode = look?.mode ?? (pendingImages ? 'images' : 'none');

  const choose = (next: string) => {
    setPendingImages(next === 'images');
    if (next === 'icon') {
      onLook({ mode: 'icon', icon: ICON_KEYS[0] ?? 'power' });
    } else if (look !== undefined && !(next === 'images' && look.mode === 'images')) {
      // Leaving an icon or images look; nothing to write when there was no look.
      onLook(undefined);
    }
  };

  return (
    <fieldset className="pe-group">
      <legend>Look</legend>
      <Field label="Show">
        <select className="input" value={mode} onChange={(e) => choose(e.target.value)}>
          <option value="none">Label only</option>
          <option value="icon">Icon</option>
          <option value="images">Images</option>
        </select>
      </Field>
      {look?.mode === 'icon' && (
        <Field label="Icon">
          <IconSelect label="Icon" value={look.icon} onChange={(icon) => onLook(icon ? { mode: 'icon', icon } : undefined)} />
        </Field>
      )}
      {mode === 'images' && (
        <>
          <p className="pe-sub">Image</p>
          <AssetPicker
            label="Image"
            assets={assets}
            value={look?.mode === 'images' ? look.image : undefined}
            noneLabel="None"
            onChange={(image) =>
              onLook(image ? { mode: 'images', image, ...(look?.mode === 'images' && look.on ? { on: look.on } : {}) } : undefined)
            }
          />
          {look?.mode === 'images' && (
            <>
              <p className="pe-sub">Image when on (optional)</p>
              <AssetPicker
                label="Image when on"
                assets={assets}
                value={look.on}
                noneLabel="Same image"
                onChange={(on) => onLook({ mode: 'images', image: look.image, ...(on ? { on } : {}) })}
              />
            </>
          )}
        </>
      )}
    </fieldset>
  );
}

function ToggleFields({ widget, catalog, assets, edit }: { widget: ToggleWidget; catalog: Catalog; assets: readonly AssetInfo[]; edit: Edit<ToggleWidget> }) {
  return (
    <>
      <Field label="Action">
        <ActionSelect label="Action" catalog={catalog} value={widget.action} onChange={(action) => edit((w) => ({ ...w, action }))} />
      </Field>
      <LookFields look={widget.look} assets={assets} onLook={(look) => edit((w) => ({ ...w, look }))} />
    </>
  );
}

function EncoderFields({ widget, catalog, edit }: { widget: EncoderWidget; catalog: Catalog; edit: Edit<EncoderWidget> }) {
  return (
    <>
      <Field label="Minus action">
        <ActionSelect label="Minus action" catalog={catalog} value={widget.minus} onChange={(minus) => edit((w) => ({ ...w, minus }))} />
      </Field>
      <Field label="Plus action">
        <ActionSelect label="Plus action" catalog={catalog} value={widget.plus} onChange={(plus) => edit((w) => ({ ...w, plus }))} />
      </Field>
    </>
  );
}

function DualFields({ widget, catalog, assets, edit }: { widget: DualActionWidget; catalog: Catalog; assets: readonly AssetInfo[]; edit: Edit<DualActionWidget> }) {
  // A behaviour widget has fixed actions and hold mode (decisions §5): they are shown, not offered.
  const locked = widget.behavior !== undefined;
  return (
    <>
      <Field label="Tap action">
        <ActionSelect label="Tap action" catalog={catalog} noneLabel="None" disabled={locked} value={widget.tap} onChange={(tap) => edit((w) => ({ ...w, tap }))} />
      </Field>
      <Field label="Hold action">
        <ActionSelect label="Hold action" catalog={catalog} noneLabel="None" disabled={locked} value={widget.hold} onChange={(hold) => edit((w) => ({ ...w, hold }))} />
      </Field>
      <p className="muted">Holding fires the hold action once, after half a second.</p>
      <LookFields look={widget.look} assets={assets} onLook={(look) => edit((w) => ({ ...w, look }))} />
    </>
  );
}

function SwitchFields({
  widget,
  shown,
  catalog,
  assets,
  edit,
  onLive,
}: {
  widget: SwitchWidget;
  shown: SwitchWidget;
  catalog: Catalog;
  assets: readonly AssetInfo[];
  edit: Edit<SwitchWidget>;
  onLive: (change: ((w: SwitchWidget) => SwitchWidget) | null) => void;
}) {
  // A behaviour switch keeps its actions and position count (decisions §5); labels, icons and angles stay editable.
  const locked = widget.behavior !== undefined;
  return (
    <>
      <fieldset className="pe-group">
        <legend>
          Positions ({widget.positions.length}
          {locked ? '' : ` of ${MIN_POSITIONS} to ${MAX_POSITIONS}`})
        </legend>
        {widget.positions.map((p, i) => (
          <div key={i} className="pe-position">
            <strong>{i + 1}</strong>
            <CommitText label={`Position ${i + 1} label`} placeholder="Label" maxLength={32} value={p.label ?? ''} onCommit={(label) => edit((w) => updatePosition(w, i, (q) => ({ ...q, label })))} />
            <CommitNumber label={`Position ${i + 1} angle`} {...ANGLE_RANGE} step={1} value={p.angle} onCommit={(angle) => edit((w) => updatePosition(w, i, (q) => ({ ...q, angle })))} />
            <IconSelect label={`Position ${i + 1} icon`} value={p.icon} onChange={(icon) => edit((w) => updatePosition(w, i, (q) => ({ ...q, icon })))} />
            <ActionSelect label={`Position ${i + 1} action`} catalog={catalog} disabled={locked} value={p.action} onChange={(action) => edit((w) => updatePosition(w, i, (q) => ({ ...q, action })))} />
            <button
              type="button"
              className="btn btn-quiet"
              disabled={locked || widget.positions.length <= MIN_POSITIONS}
              aria-label={`Remove position ${i + 1}`}
              onClick={() => edit((w) => removePosition(w, i) ?? w)}
            >
              Remove
            </button>
          </div>
        ))}
        <button type="button" className="btn" disabled={locked || widget.positions.length >= MAX_POSITIONS} onClick={() => edit((w) => addPosition(w) ?? w)}>
          Add position
        </button>
        <p className="muted">Angles are in degrees, 0 is straight up, positive turns clockwise.</p>
      </fieldset>
      <RotaryComposer widget={widget} shown={shown} assets={assets} onChange={edit} onLive={onLive} />
    </>
  );
}

/** State binding (decisions §11): `lit` and `led` each name one telemetry field; the catalog's field is the default. */
function StateFields({ widget, catalog, edit }: { widget: Widget; catalog: Catalog; edit: Edit<Widget> }) {
  const fields = [...new Set(catalog.actions.flatMap((a) => (a.stateField ? [a.stateField] : [])))].sort();
  const fallback = defaultStateField(catalog, mainAction(widget));
  const set = (key: keyof StateBinding, value: string | undefined) => {
    if (widget.state?.[key] !== value) {
      edit((w) => ({ ...w, state: { ...w.state, [key]: value } }));
    }
  };
  const row = (key: keyof StateBinding, label: string) => (
    <Field label={label}>
      <div className="pe-inline">
        <CommitText label={label} list={STATE_FIELDS_LIST} placeholder="No telemetry field" value={widget.state?.[key] ?? ''} onCommit={(v) => set(key, v)} />
        <button
          type="button"
          className="btn"
          disabled={fallback === undefined}
          title={fallback === undefined ? 'The main action has no default state field.' : fallback}
          onClick={() => set(key, fallback)}
        >
          Use default
        </button>
      </div>
    </Field>
  );
  return (
    <fieldset className="pe-group">
      <legend>State binding</legend>
      <datalist id={STATE_FIELDS_LIST}>
        {fields.map((f) => (
          <option key={f} value={f} />
        ))}
      </datalist>
      {row('lit', 'Lit when non-zero')}
      {row('led', 'LED green when non-zero')}
    </fieldset>
  );
}

export function Inspector({ widget, live, catalog, assets, onChange, onLive, onDuplicate, onDelete }: InspectorProps) {
  // The per-type editors receive the widget already narrowed; `edit` narrows the change the other way round.
  const edit = <W extends Widget>(change: (w: W) => W) => onChange((w) => change(w as W));
  const shown = live ?? widget;

  return (
    <div className="pe-inspector">
      <h3>{WIDGET_TYPE_NAMES[widget.type]}</h3>
      <p className="muted pe-id">Id: {widget.id}</p>
      {behaviorLine(widget) && <p className="muted">{behaviorLine(widget)}</p>}
      <div className="row-actions">
        <button type="button" className="btn" onClick={onDuplicate}>
          Duplicate
        </button>
        <button type="button" className="btn btn-danger" onClick={onDelete}>
          Delete
        </button>
      </div>

      <Field label="Label">
        <CommitText label="Label" maxLength={32} value={widget.label ?? ''} onCommit={(label) => edit((w) => ({ ...w, label }))} />
      </Field>
      {widget.type !== 'dualAction' && (
        <label className="pe-check">
          <input type="checkbox" checked={widget.longPressGuard === true} onChange={(e) => edit((w) => ({ ...w, longPressGuard: e.target.checked || undefined }))} />
          Long-press guard (hold half a second to act)
        </label>
      )}

      {widget.type === 'toggle' && <ToggleFields widget={widget} catalog={catalog} assets={assets} edit={edit} />}
      {widget.type === 'encoder' && <EncoderFields widget={widget} catalog={catalog} edit={edit} />}
      {widget.type === 'dualAction' && <DualFields widget={widget} catalog={catalog} assets={assets} edit={edit} />}
      {widget.type === 'switch' && shown.type === 'switch' && (
        <SwitchFields
          widget={widget}
          shown={shown}
          catalog={catalog}
          assets={assets}
          edit={edit}
          onLive={(change) => onLive(change ? (w) => change(w as SwitchWidget) : null)}
        />
      )}

      {behaviorOf(widget) === 'engineStartStop' ? (
        // Lit and LED come from the behaviour; a binding left by an edited file can only be removed.
        widget.state !== undefined && (
          <div className="pe-group">
            <p className="field-error">This button takes lit and LED from the engine, so it cannot have a state binding.</p>
            <button type="button" className="btn" onClick={() => edit((w) => ({ ...w, state: undefined }))}>
              Remove state binding
            </button>
          </div>
        )
      ) : (
        <StateFields widget={widget} catalog={catalog} edit={edit} />
      )}
    </div>
  );
}
