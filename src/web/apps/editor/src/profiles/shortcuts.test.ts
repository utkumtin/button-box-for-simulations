import { describe, expect, it } from 'vitest';
import { shortcutFor, type KeyInfo } from './shortcuts';

const key = (k: string, extra: Partial<KeyInfo> = {}): KeyInfo => ({
  key: k,
  ctrlKey: false,
  metaKey: false,
  shiftKey: false,
  altKey: false,
  ...extra,
});

describe('shortcutFor', () => {
  it('Ctrl+Z is undo, Ctrl+Y and Ctrl+Shift+Z are redo', () => {
    expect(shortcutFor(key('z', { ctrlKey: true }))).toEqual({ type: 'undo' });
    expect(shortcutFor(key('y', { ctrlKey: true }))).toEqual({ type: 'redo' });
    // With Shift held the browser reports the capital letter.
    expect(shortcutFor(key('Z', { ctrlKey: true, shiftKey: true }))).toEqual({ type: 'redo' });
  });

  it('Delete removes and the arrows move by one cell', () => {
    expect(shortcutFor(key('Delete'))).toEqual({ type: 'delete' });
    expect(shortcutFor(key('ArrowLeft'))).toEqual({ type: 'move', dCol: -1, dRow: 0 });
    expect(shortcutFor(key('ArrowRight'))).toEqual({ type: 'move', dCol: 1, dRow: 0 });
    expect(shortcutFor(key('ArrowUp'))).toEqual({ type: 'move', dCol: 0, dRow: -1 });
    expect(shortcutFor(key('ArrowDown'))).toEqual({ type: 'move', dCol: 0, dRow: 1 });
  });

  it('is ignored while typing, so a field keeps its own undo and cursor keys', () => {
    for (const targetTag of ['INPUT', 'textarea', 'SELECT']) {
      expect(shortcutFor(key('z', { ctrlKey: true, targetTag }))).toBeNull();
      expect(shortcutFor(key('Delete', { targetTag }))).toBeNull();
      expect(shortcutFor(key('ArrowLeft', { targetTag }))).toBeNull();
    }
    expect(shortcutFor(key('Delete', { targetEditable: true }))).toBeNull();
  });

  it('still works when a button has the focus', () => {
    expect(shortcutFor(key('Delete', { targetTag: 'BUTTON' }))).toEqual({ type: 'delete' });
  });

  it('does not treat a modified arrow or Delete as a plain one', () => {
    expect(shortcutFor(key('ArrowLeft', { ctrlKey: true }))).toBeNull();
    expect(shortcutFor(key('Delete', { shiftKey: true }))).toBeNull();
    expect(shortcutFor(key('z'))).toBeNull();
    expect(shortcutFor(key('y', { ctrlKey: true, shiftKey: true }))).toBeNull();
  });
});
