import { describe, expect, it } from 'vitest';
import { newWidgetId } from './widgetIds';

const PATTERN = /^[a-z0-9][a-z0-9-]{0,63}$/;

describe('newWidgetId', () => {
  it('numbers ids by type without a label', () => {
    expect(newWidgetId([], 'toggle')).toBe('toggle-1');
    expect(newWidgetId(['toggle-1'], 'toggle')).toBe('toggle-2');
    expect(newWidgetId([], 'dualAction')).toBe('dual-action-1');
  });

  it('uses the label slug and numbers repeats', () => {
    expect(newWidgetId([], 'toggle', 'Beacon Light')).toBe('beacon-light');
    expect(newWidgetId(['beacon-light'], 'toggle', 'Beacon Light')).toBe('beacon-light-2');
  });

  // The host refuses ids outside the pattern, so odd labels must still produce a valid, unused id.
  it.each(['ÄÖ!!', '', '   ', '!!!', '日本語', 'x'.repeat(80), '-a-', 'A'.repeat(56) + '!!!!'])(
    'produces a valid unique id for the label %j',
    (label) => {
      const taken = new Set<string>();
      for (let i = 0; i < 5; i++) {
        const id = newWidgetId(taken, 'switch', label);
        expect(id).toMatch(PATTERN);
        expect(taken.has(id)).toBe(false);
        taken.add(id);
      }
    },
  );

  it('transliterates accents', () => {
    expect(newWidgetId([], 'toggle', 'ÄÖ!!')).toBe('ao');
  });
});
