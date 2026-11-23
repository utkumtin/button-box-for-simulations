import { useEffect, useMemo, useRef, useState, type Dispatch } from 'react';
import type {
  AssetInfo,
  Catalog,
  DeviceInfo,
  DeviceTemplate,
  Grid,
  GridCell,
  Profile,
  Widget,
  WidgetState,
} from '@bbs/protocol';
import type { GestureOut, WidgetType } from '@bbs/widgets';
import { api } from '../api';
import { canRedo, canUndo } from '../lib/history';
import { currentProfile, isDirty, type Draft, type DraftAction } from '../lib/draft';
import {
  addLayout,
  addWidget,
  blankWidget,
  copyLayout,
  deleteWidget,
  duplicateWidget,
  describeWidget,
  moveWidget,
  removeLayout,
  renameProfile,
  resizeWidget,
  setGrid,
  updateWidget,
  type OpResult,
  type WidgetDraft,
} from '../lib/layoutOps';
import { findAction, GAME_NAMES } from '../lib/catalogView';
import { engineButtonPreset, headlightPreset, wiperPreset } from '../lib/presets';
import { profileProblems } from '../lib/problems';
import { testAction, type SwitchPositions } from '../lib/testAction';
import { Canvas } from './Canvas';
import { ConfirmDialog, type ConfirmRequest } from './ConfirmDialog';
import { testErrorMessage } from './errors';
import { CommitText } from './fields';
import { Inspector } from './Inspector';
import { withLiveWidget } from './inspectorOps';
import { LayoutTabs } from './LayoutTabs';
import { StatePreview } from './StatePreview';
import { addLayoutChoices, currentTab, layoutTabs, pairedTemplateIds } from './templateTabs';
import { Toolbar, type PresetButton } from './Toolbar';
import { shortcutFor } from './shortcuts';

const PRESETS = [
  { key: 'headlight', label: 'Headlight rotary', make: headlightPreset },
  { key: 'wiper', label: 'Wiper rotary', make: wiperPreset },
  { key: 'engine', label: 'Engine button', make: engineButtonPreset },
] as const;

const assetUrl = (id: string) => api.assetUrl(id);

export interface LayoutEditorProps {
  draft: Draft;
  dispatch: Dispatch<DraftAction>;
  templates: DeviceTemplate[];
  catalog: Catalog | null;
  /** null while the library has not been loaded. */
  assets: AssetInfo[] | null;
  devices: DeviceInfo[];
  isActive: boolean;
  saving: boolean;
  /** Problems the host named when it refused the last save (shown with ours until the next edit). */
  hostProblems: string[];
  onSave: () => void;
}

/** The open profile: name, template tabs, toolbar, canvas, inspector and the problems list. */
export function LayoutEditor(props: LayoutEditorProps) {
  const { draft, dispatch, templates, catalog, assets, devices, isActive } = props;
  const profile = currentProfile(draft);
  const dirty = isDirty(draft);

  const [wantedTab, setWantedTab] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [testMode, setTestMode] = useState(false);
  const [positions, setPositions] = useState<SwitchPositions>({});
  const [lastSent, setLastSent] = useState<string | null>(null);
  const [testError, setTestError] = useState<string | null>(null);
  const [previewStates, setPreviewStates] = useState<Record<string, WidgetState>>({});
  const [dim, setDim] = useState(false);
  const [live, setLive] = useState<Widget | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [copyNote, setCopyNote] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);

  useEffect(() => {
    if (notice === null) {
      return;
    }
    const timer = setTimeout(() => setNotice(null), 5000);
    return () => clearTimeout(timer);
  }, [notice]);

  const tabs = useMemo(() => layoutTabs(profile, templates), [profile, templates]);
  const tab = currentTab(tabs, wantedTab);
  const layout = profile.layouts.find((l) => l.templateId === tab) ?? null;
  const template = templates.find((t) => t.id === tab) ?? null;
  const selected = layout?.widgets.find((w) => w.id === selectedId) ?? null;
  const testing = testMode && isActive;

  const assetIds = useMemo(() => (assets ? new Set(assets.map((a) => a.id)) : undefined), [assets]);
  const problems = useMemo(
    () => (catalog ? profileProblems(profile, templates, catalog, assetIds) : []),
    [profile, templates, catalog, assetIds],
  );
  const widgetProblems = useMemo(() => {
    const map = new Map<string, string[]>();
    for (const p of problems) {
      if (p.templateId === tab && p.widgetId !== undefined) {
        map.set(p.widgetId, [...(map.get(p.widgetId) ?? []), p.message]);
      }
    }
    return map;
  }, [problems, tab]);

  const paired = useMemo(() => pairedTemplateIds(devices), [devices]);
  const addChoices = addLayoutChoices(profile, templates, paired);
  const copyTargets = templates
    .filter((t) => t.id !== tab)
    .map((t) => ({ template: t, hasLayout: profile.layouts.some((l) => l.templateId === t.id) }));

  /** Runs a pure edit on the current profile: the refusal reason is shown, an accepted result is one undo step. */
  const run = <E,>(op: (p: Profile) => OpResult<E>): Extract<OpResult<E>, { ok: true }> | null => {
    const result = op(profile);
    if (!result.ok) {
      setNotice(result.reason);
      return null;
    }
    setNotice(null);
    dispatch({ type: 'apply', edit: () => result.profile });
    return result as Extract<OpResult<E>, { ok: true }>;
  };

  const selectTab = (id: string) => {
    setWantedTab(id);
    setSelectedId(null);
    setLive(null);
  };

  const onAdd = (draftWidget: WidgetDraft) => {
    if (!template) {
      return;
    }
    const result = run((p) => addWidget(p, template, draftWidget));
    if (result) {
      setSelectedId(result.widgetId);
    }
  };

  const onPreset = (key: string) => {
    const preset = PRESETS.find((p) => p.key === key);
    if (preset && catalog) {
      const made = preset.make(catalog, assetIds ?? new Set());
      if (made) {
        onAdd(made);
      }
    }
  };

  const presetButtons: PresetButton[] = catalog
    ? PRESETS.filter((p) => p.make(catalog, assetIds ?? new Set()) !== null).map((p) => ({ key: p.key, label: p.label }))
    : [];

  const changeWidget = (change: (w: Widget) => Widget) => {
    if (selected && tab) {
      run((p) => updateWidget(p, tab, selected.id, change));
      setLive(null);
    }
  };

  const moveTo = (widgetId: string, cell: GridCell) => {
    if (template) {
      run((p) => moveWidget(p, template, widgetId, { col: cell.col, row: cell.row }));
    }
  };

  const resizeTo = (widgetId: string, cell: GridCell) => {
    if (template) {
      run((p) => resizeWidget(p, template, widgetId, cell));
    }
  };

  const removeSelected = () => {
    if (selected && tab) {
      if (run((p) => deleteWidget(p, tab, selected.id))) {
        setSelectedId(null);
      }
    }
  };

  const duplicateSelected = () => {
    if (selected && template) {
      const result = run((p) => duplicateWidget(p, template, selected.id));
      if (result) {
        setSelectedId(result.widgetId);
      }
    }
  };

  const changeGrid = (grid: Grid) => {
    if (template) {
      run((p) => setGrid(p, template, grid));
    }
  };

  const addLayoutFor = (t: DeviceTemplate) => {
    if (run((p) => addLayout(p, t))) {
      selectTab(t.id);
    }
  };

  const copyTo = (target: DeviceTemplate, replace: boolean) => {
    if (!tab) {
      return;
    }
    const result = run((p) => copyLayout(p, tab, target, replace));
    if (result) {
      selectTab(target.id);
      const flagged = result.belowMinimum;
      setCopyNote(
        flagged.length === 0
          ? null
          : `${flagged.length} widget${flagged.length === 1 ? ' is' : 's are'} below the minimum size on ${target.name} (${flagged.join(', ')}). Make them larger before saving.`,
      );
    }
  };

  const requestCopy = (target: DeviceTemplate) => {
    if (profile.layouts.some((l) => l.templateId === target.id)) {
      setConfirm({
        title: 'Replace layout?',
        message: `This profile already has a layout for ${target.name}. Copying replaces it.`,
        confirmLabel: 'Replace',
        danger: true,
        onConfirm: () => {
          setConfirm(null);
          copyTo(target, true);
        },
      });
    } else {
      copyTo(target, false);
    }
  };

  const requestRemove = () => {
    if (!tab) {
      return;
    }
    const name = tabs.find((t) => t.templateId === tab)?.name ?? tab;
    setConfirm({
      title: 'Remove layout?',
      message: `Remove the layout for ${name} and all its widgets from this profile? You can undo this until you leave the profile.`,
      confirmLabel: 'Remove',
      danger: true,
      onConfirm: () => {
        setConfirm(null);
        if (run((p) => removeLayout(p, tab))) {
          setSelectedId(null);
        }
      },
    });
  };

  const rename = (name: string) => {
    run((p) => renameProfile(p, name));
  };

  // Keyboard shortcuts act on the latest render.
  const rootRef = useRef<HTMLDivElement>(null);
  const keyHandler = (e: KeyboardEvent) => {
    // The Profiles section stays mounted (display: none) while another section is shown, so it keeps its unsaved draft. Keys pressed
    // there (arrows in the Telemetry table, Delete in Assets) must not edit the hidden layout.
    if (!rootRef.current || rootRef.current.offsetParent === null) {
      return;
    }
    const target = e.target instanceof HTMLElement ? e.target : null;
    const shortcut = shortcutFor({
      key: e.key,
      ctrlKey: e.ctrlKey,
      metaKey: e.metaKey,
      shiftKey: e.shiftKey,
      altKey: e.altKey,
      targetTag: target?.tagName,
      targetEditable: target?.isContentEditable,
    });
    if (!shortcut || confirm) {
      return;
    }
    if (shortcut.type === 'undo' || shortcut.type === 'redo') {
      e.preventDefault();
      dispatch({ type: shortcut.type });
    } else if (testing || !selected) {
      return;
    } else if (shortcut.type === 'delete') {
      e.preventDefault();
      removeSelected();
    } else if (layout) {
      e.preventDefault();
      const col = selected.cell.col + shortcut.dCol;
      const row = selected.cell.row + shortcut.dRow;
      // A step outside the grid is simply not taken; it is not worth a message.
      if (col >= 0 && row >= 0 && col + selected.cell.colSpan <= layout.grid.columns && row + selected.cell.rowSpan <= layout.grid.rows) {
        moveTo(selected.id, { ...selected.cell, col, row });
      }
    }
  };
  const handlerRef = useRef(keyHandler);
  useEffect(() => {
    handlerRef.current = keyHandler;
  });
  useEffect(() => {
    const listener = (e: KeyboardEvent) => handlerRef.current(e);
    window.addEventListener('keydown', listener);
    return () => window.removeEventListener('keydown', listener);
  }, []);

  const onGesture = (gesture: GestureOut) => {
    const widget = layout?.widgets.find((w) => w.id === gesture.widgetId);
    if (!widget) {
      return;
    }
    const result = testAction(widget, gesture, positions);
    setPositions(result.positions);
    const actionId = result.actionId;
    if (actionId) {
      api.testPress(actionId).then(
        () => {
          setLastSent(actionId);
          setTestError(null);
        },
        (e: unknown) => setTestError(testErrorMessage(e)),
      );
    }
  };

  const states = useMemo<Record<string, WidgetState>>(() => {
    if (!testing) {
      return previewStates;
    }
    return Object.fromEntries(Object.entries(positions).map(([id, position]) => [id, { position }]));
  }, [testing, previewStates, positions]);

  const shownLayout = layout ? withLiveWidget(layout, live) : null;

  const hostProblemsShown = props.hostProblems;
  const saveBlocked =
    problems.length > 0 ? 'Fix the problems listed below first.' : catalog === null ? 'The catalog for this game is not loaded.' : null;
  const lastSentName = lastSent && catalog ? (findAction(catalog, lastSent)?.name ?? lastSent) : lastSent;

  const focusProblem = (templateId: string, widgetId: string | undefined) => {
    setWantedTab(templateId);
    setSelectedId(widgetId ?? null);
  };

  if (!catalog) {
    return (
      <div className="pe-center">
        <p className="field-error" role="alert">
          There is no catalog for {GAME_NAMES[profile.game]} in this version, so this profile cannot be edited.
        </p>
      </div>
    );
  }

  return (
    <div className="pe-editor" ref={rootRef}>
      <div className="pe-name-row">
        <label className="pe-field-inline">
          Profile name
          <CommitText label="Profile name" maxLength={64} value={profile.name} onCommit={rename} />
        </label>
        <span className="muted">{GAME_NAMES[profile.game]}</span>
      </div>

      <Toolbar
        canUndo={canUndo(draft.history)}
        canRedo={canRedo(draft.history)}
        onUndo={() => dispatch({ type: 'undo' })}
        onRedo={() => dispatch({ type: 'redo' })}
        hasLayout={template !== null && layout !== null}
        onAdd={(type: WidgetType) => onAdd(blankWidget(type))}
        presets={presetButtons}
        onPreset={onPreset}
        grid={layout?.grid ?? null}
        onGrid={changeGrid}
        testMode={testing}
        testAllowed={isActive}
        onTestMode={(on) => {
          setTestMode(on);
          setTestError(null);
        }}
        dirty={dirty}
        saveBlocked={saveBlocked}
        saving={props.saving}
        onSave={props.onSave}
      />

      <LayoutTabs
        tabs={tabs}
        current={tab}
        onSelect={selectTab}
        addChoices={addChoices}
        onAdd={addLayoutFor}
        copyTargets={copyTargets}
        hasLayout={layout !== null}
        onCopy={requestCopy}
        onRemove={requestRemove}
        disabled={testing}
      />

      {notice && (
        <p className="pe-notice" role="status">
          {notice}
        </p>
      )}
      {copyNote && (
        <p className="pe-notice warning" role="status">
          {copyNote}{' '}
          <button type="button" className="btn btn-quiet" onClick={() => setCopyNote(null)}>
            Dismiss
          </button>
        </p>
      )}
      {testing && (
        <div className="pe-test-banner" role="status">
          Test mode: presses are sent to the game.
          {lastSentName && <span> Last sent: {lastSentName}</span>}
          {testError && <span className="field-error"> {testError}</span>}
        </div>
      )}

      <div className="pe-work">
        <div className="pe-stage">
          {layout && template && shownLayout ? (
            <>
              <Canvas
                template={template}
                layout={shownLayout}
                selectedId={selected?.id ?? null}
                onSelect={setSelectedId}
                problems={widgetProblems}
                testMode={testing}
                states={states}
                dim={dim}
                onGesture={onGesture}
                assetUrl={assetUrl}
                onMove={moveTo}
                onResize={resizeTo}
              />
              <StatePreview
                widget={selected}
                state={selected ? (previewStates[selected.id] ?? {}) : {}}
                onState={(s) => selected && setPreviewStates((old) => ({ ...old, [selected.id]: s }))}
                dim={dim}
                onDim={setDim}
              />
            </>
          ) : layout ? (
            <p className="field-error">The device template of this layout is not known to the host. You can remove the layout.</p>
          ) : (
            <p className="muted">Add a layout for a device to start placing widgets.</p>
          )}

          {(problems.length > 0 || hostProblemsShown.length > 0) && (
            <div className="pe-problems" role="alert">
              <h4>Problems that stop saving</h4>
              <ul>
                {problems.map((p, i) => (
                  <li key={`${p.templateId}-${p.widgetId ?? ''}-${i}`}>
                    <button type="button" className="pe-link" onClick={() => focusProblem(p.templateId, p.widgetId)}>
                      {p.message}
                    </button>
                  </li>
                ))}
                {hostProblemsShown.map((m, i) => (
                  <li key={`host-${i}`}>The host says: {m}</li>
                ))}
              </ul>
            </div>
          )}
        </div>

        <aside className="pe-side" aria-label="Inspector">
          {selected && !testing ? (
            <Inspector
              key={`${tab ?? ''}/${selected.id}`}
              widget={selected}
              live={live && live.id === selected.id ? live : null}
              catalog={catalog}
              assets={assets ?? []}
              onChange={changeWidget}
              onLive={(change) => setLive(change ? change(selected) : null)}
              onDuplicate={duplicateSelected}
              onDelete={removeSelected}
            />
          ) : (
            <p className="muted">
              {testing
                ? 'Click widgets on the canvas to press them in the game.'
                : layout && layout.widgets.length > 0
                  ? `Select a widget to edit it${layout.widgets[0] ? ` (for example ${describeWidget(layout.widgets[0])})` : ''}.`
                  : 'Add a widget with the buttons above.'}
            </p>
          )}
        </aside>
      </div>

      {confirm && <ConfirmDialog request={confirm} onCancel={() => setConfirm(null)} />}
    </div>
  );
}
