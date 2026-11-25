import type { Layout, Profile, Widget } from '@bbs/protocol';
import { describe, expect, it } from 'vitest';
import { behaviorLine, behaviorLockViolation, behaviorShapeProblem } from './behavior';
import { addWidget, duplicateWidget, updateWidget, type OpResult } from './layoutOps';
import { engineButtonPreset, headlightPreset, wiperPreset } from './presets';
import { layoutProblems } from './problems';
import { CATALOG, IPHONE, emptyLayout, profileWith } from './testFixtures';

const NONE = new Set<string>();

type SwitchW = Extract<Widget, { type: 'switch' }>;
type DualW = Extract<Widget, { type: 'dualAction' }>;

/** A profile holding the three presets, with their ids. */
function withPresets(): { profile: Profile; lights: string; wipers: string; engine: string } {
  let profile = profileWith(emptyLayout(8, 3));
  const ids: string[] = [];
  for (const draft of [headlightPreset(CATALOG, NONE), wiperPreset(CATALOG, NONE), engineButtonPreset(CATALOG, NONE)]) {
    const r: OpResult<{ widgetId: string }> = addWidget(profile, IPHONE, draft!);
    if (!r.ok) {
      throw new Error(r.reason);
    }
    profile = r.profile;
    ids.push(r.widgetId);
  }
  return { profile, lights: ids[0]!, wipers: ids[1]!, engine: ids[2]! };
}

const edit = (p: Profile, id: string, change: (w: Widget) => Widget) => updateWidget(p, IPHONE.id, id, change);
const widgetOf = (p: Profile, id: string): Widget => p.layouts[0]!.widgets.find((w) => w.id === id)!;
const refusal = (r: OpResult): string => {
  if (r.ok) {
    throw new Error('expected a refusal');
  }
  return r.reason;
};
const sw = (w: Widget): SwitchW => w as SwitchW;
const dual = (w: Widget): DualW => w as DualW;

describe('presets keep their behaviour through addWidget (normalizeWidget must not drop it)', () => {
  it('stores behavior on all three', () => {
    const { profile, lights, wipers, engine } = withPresets();
    expect(widgetOf(profile, lights)).toMatchObject({ behavior: 'headlights' });
    expect(widgetOf(profile, wipers)).toMatchObject({ behavior: 'wipers' });
    expect(widgetOf(profile, engine)).toMatchObject({ behavior: 'engineStartStop' });
  });

  it('a duplicate keeps the behaviour', () => {
    const { profile, lights } = withPresets();
    const r = duplicateWidget(profile, IPHONE, lights);
    if (!r.ok) {
      throw new Error(r.reason);
    }
    expect(widgetOf(r.profile, r.widgetId)).toMatchObject({ behavior: 'headlights' });
  });
});

describe('locks: refused edits are refused with a reason and leave the profile untouched', () => {
  const cases: [string, 'lights' | 'wipers' | 'engine', (w: Widget) => Widget][] = [
    ['change a headlight position action', 'lights', (w) => ({ ...sw(w), positions: sw(w).positions.map((p, i) => (i === 3 ? { ...p, action: 'light-off' } : p)) as SwitchW['positions'] })],
    ['change a wiper position action', 'wipers', (w) => ({ ...sw(w), positions: sw(w).positions.map((p, i) => (i === 0 ? { ...p, action: 'wipers-fast' } : p)) as SwitchW['positions'] })],
    ['remove a position', 'lights', (w) => ({ ...sw(w), positions: sw(w).positions.slice(0, 3) as SwitchW['positions'] })],
    ['add a position', 'wipers', (w) => ({ ...sw(w), positions: [...sw(w).positions, { angle: 90, action: 'wipers-off' }] as SwitchW['positions'] })],
    ['remove the behaviour', 'lights', (w) => ({ ...sw(w), behavior: undefined })],
    ['switch to another behaviour', 'lights', (w) => ({ ...sw(w), behavior: 'wipers' })],
    ['change the tap action', 'engine', (w) => ({ ...dual(w), tap: 'beacon' })],
    ['change the hold action', 'engine', (w) => ({ ...dual(w), hold: 'beacon' })],
    ['clear the hold action', 'engine', (w) => ({ ...dual(w), hold: undefined })],
    ['change the hold mode', 'engine', (w) => ({ ...dual(w), holdMode: 'starterTakeover' })],
    ['set a state binding', 'engine', (w) => ({ ...w, state: { lit: 'truck.electric.enabled' } })],
  ];

  for (const [what, which, change] of cases) {
    it(`${which}: ${what}`, () => {
      const setup = withPresets();
      const before = structuredClone(setup.profile);
      const result = edit(setup.profile, setup[which], change);
      expect(refusal(result).length).toBeGreaterThan(0);
      expect(setup.profile).toEqual(before);
    });
  }

  it('refuses to add a behaviour to a plain widget', () => {
    const r = addWidget(profileWith(emptyLayout(8, 3)), IPHONE, { type: 'dualAction', holdMode: 'fire', tap: 'ignition-on', hold: 'starter' });
    if (!r.ok) {
      throw new Error(r.reason);
    }
    expect(refusal(edit(r.profile, r.widgetId, (w) => ({ ...dual(w), behavior: 'engineStartStop' })))).toMatch(/preset/i);
  });
});

describe('locks: allowed edits still work', () => {
  it('label, angle, icon, position label and long-press guard of a headlight switch', () => {
    const { profile, lights } = withPresets();
    const r = edit(profile, lights, (w) => ({
      ...sw(w),
      label: 'BEAMS',
      longPressGuard: true,
      positions: sw(w).positions.map((p, i) => (i === 3 ? { ...p, angle: 45, icon: 'car-light-dimmed', label: 'H' } : p)) as SwitchW['positions'],
    }));
    if (!r.ok) {
      throw new Error(r.reason);
    }
    expect(widgetOf(r.profile, lights)).toMatchObject({ behavior: 'headlights', label: 'BEAMS', longPressGuard: true });
  });

  it('engine: label and look change, and an existing state binding can be removed', () => {
    const { profile, engine } = withPresets();
    const r = edit(profile, engine, (w) => ({ ...dual(w), label: 'START', look: { mode: 'icon', icon: 'power' } }));
    if (!r.ok) {
      throw new Error(r.reason);
    }
    expect(widgetOf(r.profile, engine)).toMatchObject({ label: 'START', behavior: 'engineStartStop' });

    // An edited file may carry a binding; the user can only take it away.
    const bound = structuredClone(profile);
    const layout = bound.layouts[0] as Layout;
    layout.widgets = layout.widgets.map((w) => (w.id === engine ? { ...w, state: { lit: 'truck.electric.enabled' } } : w));
    const removed = edit(bound, engine, (w) => ({ ...w, state: undefined }));
    if (!removed.ok) {
      throw new Error(removed.reason);
    }
    expect(widgetOf(removed.profile, engine).state).toBeUndefined();
  });

  it('a plain switch is not locked', () => {
    const r = addWidget(profileWith(emptyLayout(8, 3)), IPHONE, {
      type: 'switch',
      positions: [
        { angle: -30, action: 'light-off' },
        { angle: 30, action: 'light-on' },
      ],
    });
    if (!r.ok) {
      throw new Error(r.reason);
    }
    const next = edit(r.profile, r.widgetId, (w) => ({ ...sw(w), positions: [...sw(w).positions, { angle: 60, action: 'light-park' }] as SwitchW['positions'] }));
    expect(next.ok).toBe(true);
  });
});

describe('behaviorShapeProblem / layoutProblems', () => {
  const problemsFor = (widget: Widget, game: 'ets2' | 'ats' | 'acc' = 'ets2') =>
    layoutProblems({ ...emptyLayout(8, 3), widgets: [widget] }, IPHONE, { ...CATALOG, game }).map((p) => p.message);
  const lights = (): SwitchW => {
    const { profile, lights: id } = withPresets();
    return structuredClone(sw(widgetOf(profile, id)));
  };
  const engine = (): DualW => {
    const { profile, engine: id } = withPresets();
    return structuredClone(dual(widgetOf(profile, id)));
  };

  it('a valid preset is clean and a widget without a behaviour is unaffected', () => {
    const { profile, lights: l, wipers, engine: e } = withPresets();
    for (const id of [l, wipers, e]) {
      expect(behaviorShapeProblem(widgetOf(profile, id), 'ets2')).toBeNull();
    }
    expect(layoutProblems(profile.layouts[0]!, IPHONE, CATALOG, NONE)).toEqual([]);
    const plain: Widget = { id: 'sw', type: 'switch', cell: { col: 0, row: 0, colSpan: 2, rowSpan: 2 }, positions: [{ angle: 0, action: 'light-off' }, { angle: 30, action: 'light-on' }] };
    expect(behaviorShapeProblem(plain, 'acc')).toBeNull();
  });

  it('flags a wrong position count', () => {
    const w = lights();
    w.positions = w.positions.slice(0, 3) as SwitchW['positions'];
    expect(problemsFor(w).join('\n')).toMatch(/exactly 4 positions/);
  });

  it('flags a wrong action or order', () => {
    const w = lights();
    w.positions = w.positions.map((p, i) => (i === 2 ? { ...p, action: 'light-park' } : p)) as SwitchW['positions'];
    expect(problemsFor(w).join('\n')).toMatch(/headlights behaviour, which needs the actions/);
    expect(problemsFor({ ...lights(), behavior: 'wipers' }).join('\n')).toMatch(/wipers behaviour, which needs the actions/);
  });

  it('flags an engine button with a wrong tap, hold or hold mode', () => {
    for (const change of [{ tap: 'beacon' }, { hold: undefined }, { holdMode: 'starterTakeover' as const }]) {
      expect(problemsFor({ ...engine(), ...change }).join('\n')).toMatch(/engineStartStop behaviour/);
    }
  });

  it('flags a state binding on the engine button but allows lit on headlights', () => {
    expect(problemsFor({ ...engine(), state: { led: 'truck.engine.enabled' } }).join('\n')).toMatch(/cannot have a state binding/);
    expect(problemsFor({ ...lights(), state: { lit: 'truck.light.beacon' } })).toEqual([]);
  });

  it('flags a behaviour on a profile that is not ETS2 or ATS', () => {
    expect(problemsFor(lights(), 'acc').join('\n')).toMatch(/only ETS2 and ATS/);
    expect(problemsFor(lights(), 'ats')).toEqual([]);
  });

  it('flags a behaviour on the wrong widget type', () => {
    const w = { ...engine(), behavior: 'headlights' } as unknown as Widget;
    expect(problemsFor(w).join('\n')).toMatch(/does not fit/);
  });
});

describe('behaviorLine / behaviorLockViolation', () => {
  it('the inspector line names the behaviour; plain widgets have none', () => {
    const { profile, lights } = withPresets();
    expect(behaviorLine(widgetOf(profile, lights))).toBe('Behaviour: headlights (ETS2/ATS)');
    expect(behaviorLine({ id: 't', type: 'toggle', action: 'beacon', cell: { col: 0, row: 0, colSpan: 1, rowSpan: 1 } })).toBeNull();
  });

  it('identical widgets are allowed', () => {
    const { profile, lights } = withPresets();
    const w = widgetOf(profile, lights);
    expect(behaviorLockViolation(w, structuredClone(w))).toBeNull();
  });
});
