import type { Grid } from '@bbs/protocol';
import type { WidgetType } from '@bbs/widgets';
import { MAX_COLUMNS, MAX_ROWS, MIN_COLUMNS, MIN_ROWS, WIDGET_TYPE_NAMES } from '../lib/layoutOps';
import { CommitNumber } from './fields';

export interface PresetButton {
  key: string;
  label: string;
}

const ADDABLE: readonly WidgetType[] = ['toggle', 'encoder', 'switch', 'dualAction'];

export interface ToolbarProps {
  canUndo: boolean;
  canRedo: boolean;
  onUndo: () => void;
  onRedo: () => void;
  /** False while the profile has no layout tab to add widgets to. */
  hasLayout: boolean;
  onAdd: (type: WidgetType) => void;
  presets: PresetButton[];
  onPreset: (key: string) => void;
  grid: Grid | null;
  onGrid: (grid: Grid) => void;
  testMode: boolean;
  /** False for a profile that is not the active one: the switch is disabled with a hint. */
  testAllowed: boolean;
  onTestMode: (on: boolean) => void;
  dirty: boolean;
  /** Why saving is not possible right now, or null when it is. */
  saveBlocked: string | null;
  saving: boolean;
  onSave: () => void;
}

export function Toolbar(p: ToolbarProps) {
  const saveDisabled = p.saving || p.saveBlocked !== null;
  return (
    <div className="pe-toolbar" role="toolbar" aria-label="Editor tools">
      <div className="pe-tool-group">
        <button type="button" className="btn" disabled={!p.canUndo} onClick={p.onUndo} title="Undo (Ctrl+Z)">
          Undo
        </button>
        <button type="button" className="btn" disabled={!p.canRedo} onClick={p.onRedo} title="Redo (Ctrl+Y)">
          Redo
        </button>
      </div>

      <div className="pe-tool-group" aria-label="Add widget">
        {ADDABLE.map((type) => (
          <button key={type} type="button" className="btn" disabled={!p.hasLayout || p.testMode} onClick={() => p.onAdd(type)}>
            + {WIDGET_TYPE_NAMES[type]}
          </button>
        ))}
      </div>

      {p.presets.length > 0 && (
        <div className="pe-tool-group" aria-label="Presets">
          {p.presets.map((preset) => (
            <button key={preset.key} type="button" className="btn" disabled={!p.hasLayout || p.testMode} onClick={() => p.onPreset(preset.key)}>
              {preset.label}
            </button>
          ))}
        </div>
      )}

      {p.grid && (
        <div className="pe-tool-group">
          <label className="pe-field-inline">
            Columns
            <CommitNumber label="Columns" min={MIN_COLUMNS} max={MAX_COLUMNS} step={1} value={p.grid.columns} onCommit={(columns) => p.onGrid({ columns: Math.round(columns), rows: p.grid?.rows ?? 1 })} />
          </label>
          <label className="pe-field-inline">
            Rows
            <CommitNumber label="Rows" min={MIN_ROWS} max={MAX_ROWS} step={1} value={p.grid.rows} onCommit={(rows) => p.onGrid({ columns: p.grid?.columns ?? 1, rows: Math.round(rows) })} />
          </label>
        </div>
      )}

      <div className="pe-tool-group pe-tool-end">
        <label className="pe-check" title={p.testAllowed ? 'Presses are sent to the game.' : 'Test mode works on the active profile only.'}>
          <input type="checkbox" checked={p.testMode} disabled={!p.testAllowed} onChange={(e) => p.onTestMode(e.target.checked)} />
          Test mode
        </label>
        {!p.testAllowed && <span className="muted">Test mode works on the active profile only.</span>}
        {p.dirty && <span className="pe-unsaved">Unsaved changes</span>}
        <button type="button" className="btn btn-primary" disabled={saveDisabled || !p.dirty} title={p.saveBlocked ?? undefined} onClick={p.onSave}>
          {p.saving ? 'Saving...' : 'Save'}
        </button>
      </div>
    </div>
  );
}
