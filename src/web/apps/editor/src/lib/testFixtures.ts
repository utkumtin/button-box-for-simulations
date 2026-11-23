import type { Catalog, DeviceTemplate, Layout, Profile } from '@bbs/protocol';

/** Shared test data for the lib tests (not imported by the app). Numbers copied by hand, not read from /templates. */

/** iPhone 14 Pro: usable area 734 x 373 pt. */
export const IPHONE: DeviceTemplate = {
  schemaVersion: 1,
  id: 'iphone-14-pro',
  name: 'iPhone 14 Pro',
  screen: { width: 852, height: 393, safeArea: { top: 0, right: 59, bottom: 20, left: 59 }, pixelRatio: 3 },
  cutout: 'dynamicIsland',
  generic: false,
};

/** A small phone: usable 568 x 320 pt. */
export const SMALL: DeviceTemplate = {
  schemaVersion: 1,
  id: 'generic-568x320',
  name: 'Small phone',
  screen: { width: 568, height: 320, safeArea: { top: 0, right: 0, bottom: 0, left: 0 }, pixelRatio: 2 },
  cutout: 'none',
  generic: true,
};

export const CATALOG: Catalog = {
  schemaVersion: 1,
  game: 'ets2',
  actions: [
    { id: 'beacon', name: 'Beacon', output: { kind: 'scs', input: 'lightbeacon' }, stateField: 'truck.light.beacon' },
    { id: 'hazard', name: 'Hazard', output: { kind: 'scs', input: 'hazard' } },
    { id: 'ignition-on', name: 'Ignition on', output: { kind: 'scs', input: 'ignitionon' } },
    { id: 'starter', name: 'Starter', output: { kind: 'scs', input: 'ignitionstrt' } },
    { id: 'light-off', name: 'Lights off', output: { kind: 'scs', input: 'lightoff' } },
    { id: 'light-park', name: 'Lights parking', output: { kind: 'scs', input: 'lightpark' } },
    { id: 'light-on', name: 'Lights on', output: { kind: 'scs', input: 'lighton' } },
    { id: 'wipers-off', name: 'Wipers off', output: { kind: 'scs', input: 'wipers0' } },
    { id: 'wipers-auto', name: 'Wipers auto', output: { kind: 'scs', input: 'wipers1' } },
    { id: 'wipers-low', name: 'Wipers low', output: { kind: 'scs', input: 'wipers2' } },
    { id: 'wipers-fast', name: 'Wipers fast', output: { kind: 'scs', input: 'wipers3' } },
  ],
};

export function emptyLayout(columns = 8, rows = 3, templateId = IPHONE.id): Layout {
  return { schemaVersion: 1, templateId, grid: { columns, rows }, widgets: [] };
}

export function profileWith(layout: Layout = emptyLayout()): Profile {
  return { schemaVersion: 1, id: 'test-1234', name: 'Test', game: 'ets2', layouts: [layout] };
}
