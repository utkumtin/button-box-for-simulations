import type { DeviceInfo, DeviceTemplate, Profile } from '@bbs/protocol';

/** Which template tabs a profile has and which layouts can still be added (decisions §6, §11). */

export interface LayoutTab {
  templateId: string;
  /** The template's name; the id for a template the host does not know. */
  name: string;
  known: boolean;
}

/** One tab per layout, in the order the profile stores them (stable while editing). */
export function layoutTabs(profile: Profile, templates: readonly DeviceTemplate[]): LayoutTab[] {
  return profile.layouts.map((layout) => {
    const template = templates.find((t) => t.id === layout.templateId);
    return { templateId: layout.templateId, name: template?.name ?? layout.templateId, known: template !== undefined };
  });
}

export interface AddLayoutChoice {
  template: DeviceTemplate;
  /** A paired device uses this template. */
  paired: boolean;
}

export function pairedTemplateIds(devices: readonly DeviceInfo[]): Set<string> {
  return new Set(devices.map((d) => d.templateId));
}

/** Templates without a layout yet: shipped ones before generic ones, each group by name. */
export function addLayoutChoices(
  profile: Profile,
  templates: readonly DeviceTemplate[],
  paired: ReadonlySet<string>,
): AddLayoutChoice[] {
  const have = new Set(profile.layouts.map((l) => l.templateId));
  return templates
    .filter((t) => !have.has(t.id))
    .map((template) => ({ template, paired: paired.has(template.id) }))
    .sort((a, b) => {
      if (a.template.generic !== b.template.generic) {
        return a.template.generic ? 1 : -1;
      }
      return a.template.name.localeCompare(b.template.name);
    });
}

/** The tab to show after the profile changed: the wanted one if it still exists, else the first, else none. */
export function currentTab(tabs: readonly LayoutTab[], wanted: string | null): string | null {
  return tabs.find((t) => t.templateId === wanted)?.templateId ?? tabs[0]?.templateId ?? null;
}
