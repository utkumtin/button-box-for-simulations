import type { Led, Widget, WidgetState } from '@bbs/protocol';
import { litLabel, previewControls } from '../lib/previewControls';

/**
 * Preview controls (decisions §11): what the selected widget looks like lit, with an LED, or at a switch position,
 * and the look without telemetry. Editor only; nothing here is saved or sent to a phone.
 */
export function StatePreview({
  widget,
  state,
  onState,
  dim,
  onDim,
}: {
  widget: Widget | null;
  state: WidgetState;
  onState: (state: WidgetState) => void;
  dim: boolean;
  onDim: (dim: boolean) => void;
}) {
  const controls = widget ? previewControls(widget) : null;
  const hasLit = controls?.lit === true;
  const hasLed = controls?.led === true;
  const isSwitch = controls?.position === true;
  return (
    <div className="pe-preview-bar">
      <strong>Preview</strong>
      <label className="pe-check">
        <input type="checkbox" checked={dim} onChange={(e) => onDim(e.target.checked)} />
        No telemetry look
      </label>
      {widget && hasLit && (
        <label className="pe-check">
          <input type="checkbox" checked={state.lit === true} onChange={(e) => onState({ ...state, lit: e.target.checked })} />
          {litLabel(widget)}
        </label>
      )}
      {widget && hasLed && (
        <label className="pe-field-inline">
          LED
          <select className="input" value={state.led ?? 'off'} onChange={(e) => onState({ ...state, led: e.target.value as Led })}>
            <option value="off">Off</option>
            <option value="green">Green</option>
            <option value="amber">Amber</option>
          </select>
        </label>
      )}
      {widget && widget.type === 'switch' && isSwitch && (
        <label className="pe-field-inline">
          Position
          <select className="input" value={state.position ?? 0} onChange={(e) => onState({ ...state, position: Number(e.target.value) })}>
            {widget.positions.map((p, i) => (
              <option key={i} value={i}>
                {i + 1}
                {p.label ? ` ${p.label}` : ''}
              </option>
            ))}
          </select>
        </label>
      )}
      {widget && !hasLit && !hasLed && !isSwitch && <span className="muted">Bind a state field to preview lit or LED.</span>}
      {!widget && <span className="muted">Select a widget to preview its state.</span>}
    </div>
  );
}
