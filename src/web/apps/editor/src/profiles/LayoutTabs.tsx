import type { DeviceTemplate } from '@bbs/protocol';
import type { AddLayoutChoice, LayoutTab } from './templateTabs';

const choiceLabel = (c: AddLayoutChoice): string =>
  `${c.template.name}${c.paired ? ' (paired device)' : ''}${c.template.generic ? ' (generic)' : ''}`;

/** One tab per template layout, with Add layout, Copy to and Remove (decisions §6, §11). */
export function LayoutTabs({
  tabs,
  current,
  onSelect,
  addChoices,
  onAdd,
  copyTargets,
  hasLayout,
  onCopy,
  onRemove,
  disabled,
}: {
  tabs: LayoutTab[];
  current: string | null;
  onSelect: (templateId: string) => void;
  addChoices: AddLayoutChoice[];
  onAdd: (template: DeviceTemplate) => void;
  /** Every known template except the current one, with whether the profile already has a layout there. */
  copyTargets: { template: DeviceTemplate; hasLayout: boolean }[];
  hasLayout: boolean;
  onCopy: (template: DeviceTemplate) => void;
  onRemove: () => void;
  disabled: boolean;
}) {
  return (
    <div className="pe-tabs">
      <div className="pe-tab-list" role="tablist" aria-label="Device layouts">
        {tabs.map((t) => (
          <button
            key={t.templateId}
            type="button"
            role="tab"
            aria-selected={t.templateId === current}
            className={t.templateId === current ? 'pe-tab is-current' : 'pe-tab'}
            onClick={() => onSelect(t.templateId)}
          >
            {t.name}
            {!t.known && ' (unknown device)'}
          </button>
        ))}
        {tabs.length === 0 && <span className="muted">This profile has no layout yet.</span>}
      </div>
      <div className="pe-tab-actions">
        <select
          className="input"
          aria-label="Add layout"
          value=""
          disabled={disabled || addChoices.length === 0}
          onChange={(e) => {
            const choice = addChoices.find((c) => c.template.id === e.target.value);
            if (choice) {
              onAdd(choice.template);
            }
          }}
        >
          <option value="">Add layout...</option>
          {addChoices.map((c) => (
            <option key={c.template.id} value={c.template.id}>
              {choiceLabel(c)}
            </option>
          ))}
        </select>
        <select
          className="input"
          aria-label="Copy layout to"
          value=""
          disabled={disabled || !hasLayout || copyTargets.length === 0}
          onChange={(e) => {
            const target = copyTargets.find((c) => c.template.id === e.target.value);
            if (target) {
              onCopy(target.template);
            }
          }}
        >
          <option value="">Copy to...</option>
          {copyTargets.map((c) => (
            <option key={c.template.id} value={c.template.id}>
              {c.template.name}
              {c.hasLayout ? ' (replaces its layout)' : ''}
            </option>
          ))}
        </select>
        <button type="button" className="btn btn-quiet" disabled={disabled || !hasLayout} onClick={onRemove}>
          Remove layout
        </button>
      </div>
    </div>
  );
}
