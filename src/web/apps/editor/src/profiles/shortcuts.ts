/**
 * Keyboard shortcuts of the canvas (decisions §11): Ctrl+Z undo, Ctrl+Y or Ctrl+Shift+Z redo, Delete removes the
 * selected widget, arrow keys move it by one cell. While the user types in a field, the keys belong to the field.
 */
export interface KeyInfo {
  key: string;
  ctrlKey: boolean;
  metaKey: boolean;
  shiftKey: boolean;
  altKey: boolean;
  /** Tag name of the event target, any case. */
  targetTag?: string | undefined;
  targetEditable?: boolean | undefined;
}

export type Shortcut = { type: 'undo' } | { type: 'redo' } | { type: 'delete' } | { type: 'move'; dCol: number; dRow: number };

const FIELD_TAGS = new Set(['INPUT', 'TEXTAREA', 'SELECT']);

export function isTypingTarget(tag: string | undefined, editable: boolean | undefined): boolean {
  return editable === true || (tag !== undefined && FIELD_TAGS.has(tag.toUpperCase()));
}

export function shortcutFor(k: KeyInfo): Shortcut | null {
  if (isTypingTarget(k.targetTag, k.targetEditable)) {
    return null;
  }
  const mod = k.ctrlKey || k.metaKey;
  const key = k.key.toLowerCase();
  if (mod && !k.altKey) {
    if (key === 'z') {
      return k.shiftKey ? { type: 'redo' } : { type: 'undo' };
    }
    return key === 'y' && !k.shiftKey ? { type: 'redo' } : null;
  }
  if (mod || k.altKey || k.shiftKey) {
    return null;
  }
  switch (k.key) {
    case 'Delete':
      return { type: 'delete' };
    case 'ArrowLeft':
      return { type: 'move', dCol: -1, dRow: 0 };
    case 'ArrowRight':
      return { type: 'move', dCol: 1, dRow: 0 };
    case 'ArrowUp':
      return { type: 'move', dCol: 0, dRow: -1 };
    case 'ArrowDown':
      return { type: 'move', dCol: 0, dRow: 1 };
    default:
      return null;
  }
}
