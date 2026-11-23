import type { WidgetType } from '@bbs/widgets';

const TYPE_BASE: Record<WidgetType, string> = {
  toggle: 'toggle',
  encoder: 'encoder',
  switch: 'switch',
  dualAction: 'dual-action',
};

// Leaves room for a "-NNN" suffix inside the schema's 64 characters.
const MAX_BASE = 56;

/** Lower-case ASCII slug of a label: accents dropped, other characters become single dashes. */
export function slug(text: string): string {
  return text
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, MAX_BASE)
    .replace(/-+$/g, '');
}

/**
 * A widget id unique among `existing`, matching ^[a-z0-9][a-z0-9-]{0,63}$. Without a usable label the id is
 * `<type>-<n>` (toggle-1, toggle-2); with one it is the label slug, then `<slug>-2`, `<slug>-3`.
 */
export function newWidgetId(existing: Iterable<string>, type: WidgetType, label?: string): string {
  const taken = new Set(existing);
  const fromLabel = label ? slug(label) : '';
  if (fromLabel === '') {
    const base = TYPE_BASE[type];
    for (let n = 1; ; n++) {
      const id = `${base}-${n}`;
      if (!taken.has(id)) {
        return id;
      }
    }
  }
  if (!taken.has(fromLabel)) {
    return fromLabel;
  }
  for (let n = 2; ; n++) {
    const id = `${fromLabel}-${n}`;
    if (!taken.has(id)) {
      return id;
    }
  }
}
