import { describe, expect, it } from 'vitest';
import { HISTORY_LIMIT, canRedo, canUndo, createHistory, push, redo, undo } from './history';

describe('history', () => {
  it('undoes and redoes in order', () => {
    let h = createHistory(0);
    h = push(push(h, 1), 2);
    expect(h.present).toBe(2);
    h = undo(h);
    expect(h.present).toBe(1);
    h = undo(h);
    expect(h.present).toBe(0);
    expect(canUndo(h)).toBe(false);
    h = redo(redo(h));
    expect(h.present).toBe(2);
    expect(canRedo(h)).toBe(false);
  });

  it('does nothing when there is nothing to undo or redo', () => {
    const h = createHistory('a');
    expect(undo(h)).toBe(h);
    expect(redo(h)).toBe(h);
  });

  // A new edit after an undo starts a new future; redoing into the old one would resurrect discarded work.
  it('drops the redo branch on a new push', () => {
    let h = push(push(createHistory(0), 1), 2);
    h = undo(h);
    expect(canRedo(h)).toBe(true);
    h = push(h, 9);
    expect(canRedo(h)).toBe(false);
    expect(undo(h).present).toBe(1);
  });

  // Decisions §11: up to 100 steps. 101 edits must leave exactly 100 undos, not 101 and not fewer.
  it('keeps at most 100 undo steps', () => {
    let h = createHistory(0);
    for (let i = 1; i <= HISTORY_LIMIT + 1; i++) {
      h = push(h, i);
    }
    let undos = 0;
    while (canUndo(h)) {
      h = undo(h);
      undos++;
    }
    expect(undos).toBe(100);
    expect(h.present).toBe(1); // the oldest step (0) fell off
  });
});
