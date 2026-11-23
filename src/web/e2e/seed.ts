import { EditorApi, PHONE_TEMPLATE, hostInfo, type E2EProfile, type E2EWidget } from './host';

/**
 * The host is shared by the whole run and starts with one profile, the first-run "ETS2 starter" (decisions section 6). A spec that
 * changes profiles reads the seeded one before its tests and puts everything back after each test, so the order of the spec files
 * never matters: the content is saved back, every other profile is deleted, and the original is re-activated through a scratch
 * profile so that every switch is at position 0 again (re-activating the running profile keeps them).
 */
export async function readSeed(api = new EditorApi(hostInfo())): Promise<E2EProfile> {
  return api.profile(await api.activeId());
}

export async function restoreSeed(original: E2EProfile, api = new EditorApi(hostInfo())): Promise<void> {
  await api.save(original); // the seeded profile is still there; a test never deletes it
  const scratch = await api.duplicate(original.id);
  await api.activate(scratch.id);
  await api.activate(original.id);
  for (const p of (await api.profiles()).profiles) {
    if (p.id !== original.id) await api.remove(p.id);
  }
}

export function phoneWidget(profile: E2EProfile, id: string): E2EWidget {
  const widget = profile.layouts.find((l) => l.templateId === PHONE_TEMPLATE)?.widgets.find((w) => w.id === id);
  if (!widget) throw new Error(`The profile has no widget ${id} on the phone layout.`);
  return widget;
}
