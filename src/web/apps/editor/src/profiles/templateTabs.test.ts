import type { DeviceInfo, DeviceTemplate } from '@bbs/protocol';
import { describe, expect, it } from 'vitest';
import { emptyLayout, IPHONE, profileWith, SMALL } from '../lib/testFixtures';
import { addLayoutChoices, currentTab, layoutTabs, pairedTemplateIds } from './templateTabs';

const tpl = (id: string, name: string, generic: boolean): DeviceTemplate => ({ ...IPHONE, id, name, generic });
const PIXEL = tpl('pixel-8', 'Pixel 8', false);
const GALAXY = tpl('galaxy-s24', 'Galaxy S24', false);
const device = (templateId: string): DeviceInfo => ({
  id: `d-${templateId}`,
  name: 'Phone',
  templateId,
  connected: true,
  screenMismatch: false,
});

describe('layoutTabs', () => {
  it('has one tab per layout, in the stored order', () => {
    const profile = {
      ...profileWith(),
      layouts: [emptyLayout(8, 3, SMALL.id), emptyLayout(8, 3, IPHONE.id)],
    };
    expect(layoutTabs(profile, [IPHONE, SMALL]).map((t) => t.templateId)).toEqual([SMALL.id, IPHONE.id]);
  });

  it('keeps a layout whose template is unknown, so it can still be removed', () => {
    const tabs = layoutTabs(profileWith(emptyLayout(8, 3, 'gone')), [IPHONE]);
    expect(tabs).toEqual([{ templateId: 'gone', name: 'gone', known: false }]);
  });
});

describe('addLayoutChoices', () => {
  it('lists templates without a layout, shipped before generic, each by name', () => {
    const generic = tpl('generic-1', 'Aaa generic', true);
    const choices = addLayoutChoices(profileWith(), [generic, PIXEL, IPHONE, GALAXY], new Set());
    expect(choices.map((c) => c.template.id)).toEqual([GALAXY.id, PIXEL.id, generic.id]);
  });

  it('marks the templates of paired devices', () => {
    const paired = pairedTemplateIds([device(PIXEL.id)]);
    const choices = addLayoutChoices(profileWith(), [PIXEL, GALAXY], paired);
    expect(choices.map((c) => [c.template.id, c.paired])).toEqual([
      [GALAXY.id, false],
      [PIXEL.id, true],
    ]);
  });

  it('offers nothing when every template has a layout', () => {
    expect(addLayoutChoices(profileWith(), [IPHONE], new Set())).toEqual([]);
  });
});

describe('currentTab', () => {
  const tabs = layoutTabs({ ...profileWith(), layouts: [emptyLayout(8, 3, 'a'), emptyLayout(8, 3, 'b')] }, []);

  it('keeps the wanted tab, falls back to the first, and has none for no layouts', () => {
    expect(currentTab(tabs, 'b')).toBe('b');
    expect(currentTab(tabs, 'removed')).toBe('a');
    expect(currentTab(tabs, null)).toBe('a');
    expect(currentTab([], 'a')).toBeNull();
  });
});
