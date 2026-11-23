import type { Profile } from '@bbs/protocol';
import { type History, createHistory, push, redo, undo } from './history';

/**
 * The editing session of one profile (decisions §11): changes reach the phone only on "Save", and unsaved changes are
 * shown. `saved` is what the host has; `history.present` is what the user sees.
 */
export interface Draft {
  saved: Profile;
  history: History<Profile>;
}

export type DraftAction =
  | { type: 'load'; profile: Profile }
  /** A finished edit. An edit that returns the same object (a refused or no-op edit) records nothing. */
  | { type: 'apply'; edit: (profile: Profile) => Profile }
  | { type: 'undo' }
  | { type: 'redo' }
  /** After a successful save: `profile` is the new baseline. The history stays so undo still works. */
  | { type: 'saved'; profile: Profile };

export function createDraft(profile: Profile): Draft {
  return { saved: profile, history: createHistory(profile) };
}

export function draftReducer(state: Draft, action: DraftAction): Draft {
  switch (action.type) {
    case 'load':
      return createDraft(action.profile);
    case 'apply': {
      const next = action.edit(state.history.present);
      return next === state.history.present ? state : { ...state, history: push(state.history, next) };
    }
    case 'undo':
      return { ...state, history: undo(state.history) };
    case 'redo':
      return { ...state, history: redo(state.history) };
    case 'saved':
      return { ...state, saved: action.profile };
  }
}

/** Structural equality of plain JSON values; key order does not matter. */
export function jsonEqual(a: unknown, b: unknown): boolean {
  if (a === b) {
    return true;
  }
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) {
    return false;
  }
  if (Array.isArray(a) || Array.isArray(b)) {
    return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((v, i) => jsonEqual(v, b[i]));
  }
  const ao = a as Record<string, unknown>;
  const bo = b as Record<string, unknown>;
  const keys = Object.keys(ao).filter((k) => ao[k] !== undefined);
  const otherKeys = Object.keys(bo).filter((k) => bo[k] !== undefined);
  return keys.length === otherKeys.length && keys.every((k) => jsonEqual(ao[k], bo[k]));
}

/** Structural, not by reference: undoing back to the saved state is not dirty. */
export function isDirty(state: Draft): boolean {
  return !jsonEqual(state.history.present, state.saved);
}

export function currentProfile(state: Draft): Profile {
  return state.history.present;
}
