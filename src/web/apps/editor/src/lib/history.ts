/**
 * Generic undo/redo history (decisions §11: a reducer with up to 100 steps, no library).
 *
 * Push once per finished edit. A drag or resize in progress must NOT push per pointer move: the UI keeps a transient
 * preview while the pointer is down and pushes one entry on drop, so one undo reverts the whole gesture.
 */
export const HISTORY_LIMIT = 100;

export interface History<T> {
  /** Oldest first; the last entry is what undo returns to. */
  past: readonly T[];
  present: T;
  /** Nearest first; the first entry is what redo returns to. */
  future: readonly T[];
}

export function createHistory<T>(present: T): History<T> {
  return { past: [], present, future: [] };
}

/** Makes `next` the present. Drops the redo branch and keeps at most HISTORY_LIMIT undo steps. */
export function push<T>(history: History<T>, next: T): History<T> {
  const past = [...history.past, history.present];
  if (past.length > HISTORY_LIMIT) {
    past.splice(0, past.length - HISTORY_LIMIT);
  }
  return { past, present: next, future: [] };
}

export function canUndo<T>(history: History<T>): boolean {
  return history.past.length > 0;
}

export function canRedo<T>(history: History<T>): boolean {
  return history.future.length > 0;
}

export function undo<T>(history: History<T>): History<T> {
  if (history.past.length === 0) {
    return history;
  }
  return {
    past: history.past.slice(0, -1),
    present: history.past[history.past.length - 1] as T,
    future: [history.present, ...history.future],
  };
}

export function redo<T>(history: History<T>): History<T> {
  if (history.future.length === 0) {
    return history;
  }
  const [next, ...rest] = history.future;
  return { past: [...history.past, history.present], present: next as T, future: rest };
}
