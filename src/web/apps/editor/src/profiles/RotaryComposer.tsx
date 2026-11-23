import { useMemo, useState } from 'react';
import type { AssetInfo, Layout, SwitchWidget, Widget } from '@bbs/protocol';
import { ButtonGrid } from '@bbs/widgets';
import { api } from '../api';
import { SliderField } from './fields';
import {
  LAYER_KEYS,
  OFFSET_RANGE,
  SCALE_RANGE,
  layerValue,
  setLayerAsset,
  setLayerValue,
  type LayerKey,
  type LayerValue,
} from './inspectorOps';

const LAYER_NAMES: Record<LayerKey, string> = { base: 'Base', knob: 'Knob', highlight: 'Highlight' };
const LAYER_HINTS: Record<LayerKey, string> = {
  base: 'Static image under the knob.',
  knob: 'Turns to the selected position.',
  highlight: 'Static image over the knob.',
};

const noop = () => undefined;
const assetUrl = (id: string) => api.assetUrl(id);

/** Image choice from the library: thumbnails, plus an entry for "not used" (that layer is drawn in code). */
export function AssetPicker({
  assets,
  value,
  onChange,
  noneLabel,
  label,
}: {
  assets: readonly AssetInfo[];
  value: string | undefined;
  onChange: (id: string | undefined) => void;
  noneLabel: string;
  label: string;
}) {
  const missing = value !== undefined && !assets.some((a) => a.id === value);
  return (
    <div>
      <div className="pe-assets" role="group" aria-label={label}>
        <button type="button" className={value === undefined ? 'pe-asset is-selected' : 'pe-asset'} aria-pressed={value === undefined} onClick={() => onChange(undefined)}>
          {noneLabel}
        </button>
        {assets.map((a) => (
          <button
            key={a.id}
            type="button"
            className={a.id === value ? 'pe-asset is-selected' : 'pe-asset'}
            aria-pressed={a.id === value}
            title={`Image ${a.id.slice(0, 8)}`}
            onClick={() => onChange(a.id)}
          >
            <img src={api.assetUrl(a.id)} alt={`Image ${a.id.slice(0, 8)}`} />
          </button>
        ))}
      </div>
      {missing && <p className="field-error">The chosen image is not in the asset library.</p>}
      {assets.length === 0 && <p className="muted">The asset library is empty. Add images in the Assets section.</p>}
    </div>
  );
}

/**
 * Rotary composer (decisions §7): base, knob and highlight layers with scale and centre calibration. Sliders show
 * their value live in the canvas and in the preview below; the history gets one step when the slider is released.
 */
export function RotaryComposer({
  widget,
  shown,
  assets,
  onChange,
  onLive,
}: {
  widget: SwitchWidget;
  /** The widget as the previews show it: the live version while a slider moves, else the committed one. */
  shown: SwitchWidget;
  assets: readonly AssetInfo[];
  onChange: (change: (w: SwitchWidget) => SwitchWidget) => void;
  onLive: (change: ((w: SwitchWidget) => SwitchWidget) | null) => void;
}) {
  const [position, setPosition] = useState(0);
  const shownPosition = Math.min(position, widget.positions.length - 1);

  const layout = useMemo<Layout>(
    () => ({
      schemaVersion: 1,
      templateId: 'composer',
      grid: { columns: 1, rows: 1 },
      widgets: [{ ...shown, cell: { col: 0, row: 0, colSpan: 1, rowSpan: 1 } } as Widget],
    }),
    [shown],
  );
  const states = useMemo(() => ({ [shown.id]: { position: shownPosition } }), [shown.id, shownPosition]);

  const value = (key: LayerKey, prop: LayerValue, v: number) => {
    onChange((w) => setLayerValue(w, key, prop, v));
    onLive(null);
  };

  return (
    <div className="pe-composer">
      <h4>Rotary look</h4>
      <p className="muted">
        Position labels are drawn by the app, not baked into the images. A layer without an image is drawn in code.
      </p>
      {LAYER_KEYS.map((key) => {
        const layer = widget.rotary?.[key];
        return (
          <fieldset key={key} className="pe-layer">
            <legend>
              {LAYER_NAMES[key]} <span className="muted">{LAYER_HINTS[key]}</span>
            </legend>
            <AssetPicker
              label={`${LAYER_NAMES[key]} image`}
              assets={assets}
              value={layer?.asset}
              noneLabel="Drawn in code"
              onChange={(id) => onChange((w) => setLayerAsset(w, key, id))}
            />
            {layer && (
              <>
                <SliderField
                  label={`${LAYER_NAMES[key]} scale`}
                  value={layerValue(layer, key, 'scale')}
                  {...SCALE_RANGE}
                  step={0.01}
                  onLive={(v) => onLive((w) => setLayerValue(w, key, 'scale', v))}
                  onCommit={(v) => value(key, 'scale', v)}
                />
                <SliderField
                  label={`${LAYER_NAMES[key]} offset X`}
                  value={layerValue(layer, key, 'offsetX')}
                  {...OFFSET_RANGE}
                  step={0.01}
                  onLive={(v) => onLive((w) => setLayerValue(w, key, 'offsetX', v))}
                  onCommit={(v) => value(key, 'offsetX', v)}
                />
                <SliderField
                  label={`${LAYER_NAMES[key]} offset Y`}
                  value={layerValue(layer, key, 'offsetY')}
                  {...OFFSET_RANGE}
                  step={0.01}
                  onLive={(v) => onLive((w) => setLayerValue(w, key, 'offsetY', v))}
                  onCommit={(v) => value(key, 'offsetY', v)}
                />
              </>
            )}
          </fieldset>
        );
      })}
      <div className="pe-composer-preview">
        <div className="pe-composer-view">
          <ButtonGrid layout={layout} states={states} dim={false} enabled={false} onGesture={noop} assetUrl={assetUrl} />
        </div>
        <label className="pe-field">
          <span>Preview position</span>
          <select className="input" value={shownPosition} onChange={(e) => setPosition(Number(e.target.value))}>
            {widget.positions.map((p, i) => (
              <option key={i} value={i}>
                {i + 1}
                {p.label ? ` ${p.label}` : ''}
              </option>
            ))}
          </select>
        </label>
      </div>
    </div>
  );
}
