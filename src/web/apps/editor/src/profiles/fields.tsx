import { useState } from 'react';
import type { Catalog } from '@bbs/protocol';
import { actionLabel, findAction } from '../lib/catalogView';

/**
 * Small form controls of the inspector. Text and number fields keep what the user types in the field and hand it over
 * once, on blur or Enter, so one change is one undo step (decisions §11). When the value changes from outside (undo,
 * another selection) the field shows the new value.
 */

/** Keeps a local copy of `value` that follows it whenever it changes from outside. */
function useSynced<T>(value: T): [T, (next: T) => void] {
  const [local, setLocal] = useState(value);
  const [seen, setSeen] = useState(value);
  if (!Object.is(value, seen)) {
    setSeen(value);
    setLocal(value);
  }
  return [local, setLocal];
}

export function CommitText({
  value,
  onCommit,
  label,
  maxLength,
  placeholder,
  list,
}: {
  value: string;
  onCommit: (value: string) => void;
  label: string;
  maxLength?: number;
  placeholder?: string;
  list?: string;
}) {
  const [text, setText] = useSynced(value);
  const commit = () => {
    if (text !== value) {
      onCommit(text);
    }
  };
  return (
    <input
      className="input"
      type="text"
      aria-label={label}
      value={text}
      maxLength={maxLength}
      placeholder={placeholder}
      list={list}
      onChange={(e) => setText(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          commit();
        }
      }}
    />
  );
}

export function clampNumber(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}

export function CommitNumber({
  value,
  min,
  max,
  step,
  onCommit,
  label,
}: {
  value: number;
  min: number;
  max: number;
  step: number;
  onCommit: (value: number) => void;
  label: string;
}) {
  const [text, setText] = useSynced(String(value));
  const commit = () => {
    const parsed = Number(text);
    if (text.trim() === '' || !Number.isFinite(parsed)) {
      setText(String(value));
      return;
    }
    const next = clampNumber(parsed, min, max);
    if (next !== value) {
      onCommit(next);
    }
    // Show the stored value again: it is the new one when the edit was accepted (the prop changes) and the old one when refused.
    setText(String(value));
  };
  return (
    <input
      className="input pe-number"
      type="number"
      aria-label={label}
      value={text}
      min={min}
      max={max}
      step={step}
      onChange={(e) => setText(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          commit();
        }
      }}
    />
  );
}

/**
 * A slider with a number field. While the slider moves, `onLive` shows the value in the previews without touching the
 * history; the value is committed once, when the user lets go (one undo step).
 */
export function SliderField({
  label,
  value,
  min,
  max,
  step,
  onLive,
  onCommit,
}: {
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  onLive: (value: number) => void;
  onCommit: (value: number) => void;
}) {
  const [pending, setPending] = useState<number | null>(null);
  const finish = () => {
    if (pending !== null) {
      if (pending !== value) {
        onCommit(pending);
      }
      setPending(null);
    }
  };
  return (
    <div className="pe-slider">
      <span className="pe-slider-label">{label}</span>
      <input
        type="range"
        aria-label={label}
        min={min}
        max={max}
        step={step}
        value={pending ?? value}
        onChange={(e) => {
          const next = Number(e.target.value);
          setPending(next);
          onLive(next);
        }}
        onPointerUp={finish}
        onKeyUp={finish}
        onBlur={finish}
      />
      <CommitNumber label={`${label} (number)`} value={pending ?? value} min={min} max={max} step={step} onCommit={onCommit} />
    </div>
  );
}

/** Catalog action picker. An id that is not in the catalog stays selectable so the problem is visible, not hidden. */
export function ActionSelect({
  catalog,
  value,
  onChange,
  label,
  noneLabel,
}: {
  catalog: Catalog;
  value: string | undefined;
  onChange: (id: string) => void;
  label: string;
  /** Present for optional actions: the entry that clears it. Required actions show "Choose an action..." instead. */
  noneLabel?: string;
}) {
  const unknown = value !== undefined && value !== '' && findAction(catalog, value) === undefined;
  return (
    <select className="input" aria-label={label} value={value ?? ''} onChange={(e) => onChange(e.target.value)}>
      <option value="">{noneLabel ?? 'Choose an action...'}</option>
      {unknown && <option value={value}>Unknown action ({value})</option>}
      {catalog.actions.map((a) => (
        <option key={a.id} value={a.id}>
          {actionLabel(a)}
        </option>
      ))}
    </select>
  );
}
