import { describe, expect, it } from 'vitest';
import { addWidget } from './layoutOps';
import { engineButtonPreset, headlightPreset, wiperPreset } from './presets';
import { layoutProblems } from './problems';
import { CATALOG, IPHONE, emptyLayout, profileWith } from './testFixtures';

const BASE = '02abf8a01862694028bb6eed91cde12ab712b3e24337ef0905cbe4bcc539bff9';
const KNOB = '8e5488f4fcf1e99596ef42ef7fbc103f4d3c2af8fa4fb7b30271a4cb05af0087';
const ENGINE = 'e744710295a932027d7feef53bfc06783e4f11eb5e89145f846ea07896c52572';
const ALL = new Set([BASE, KNOB, ENGINE]);
const NONE = new Set<string>();

describe('presets', () => {
  it('headlight rotary: OFF / AUTO / LOW with the rotary images when the library has them', () => {
    const w = headlightPreset(CATALOG, ALL);
    expect(w).toMatchObject({ type: 'switch', label: 'LIGHTS' });
    if (w?.type !== 'switch') {
      throw new Error('not a switch');
    }
    expect(w.positions).toEqual([
      { label: 'OFF', angle: -30, action: 'light-off' },
      { label: 'AUTO', angle: 0, action: 'light-park' },
      { icon: 'car-light-dimmed', angle: 30, action: 'light-on' },
    ]);
    expect(w.rotary).toEqual({ base: { asset: BASE, scale: 1 }, knob: { asset: KNOB, scale: 0.55 } });
  });

  it('wiper rotary: four positions', () => {
    const w = wiperPreset(CATALOG, ALL);
    if (w?.type !== 'switch') {
      throw new Error('not a switch');
    }
    expect(w.label).toBe('WIPERS');
    expect(w.positions.map((p) => [p.label, p.angle, p.action])).toEqual([
      ['OFF', -30, 'wipers-off'],
      ['AUTO', 0, 'wipers-auto'],
      ['LOW', 30, 'wipers-low'],
      ['FAST', 60, 'wipers-fast'],
    ]);
  });

  // A missing image must not leave a dangling asset id: the host would refuse the profile.
  it('draws the rotaries in code when the images are not in the library', () => {
    for (const w of [headlightPreset(CATALOG, NONE), wiperPreset(CATALOG, NONE)]) {
      expect(w).not.toBeNull();
      expect('rotary' in (w as object)).toBe(false);
    }
    expect('rotary' in (headlightPreset(CATALOG, new Set([BASE])) as object)).toBe(false);
  });

  it('engine button: tap ignition, hold starter (fire), state bindings, image only when present', () => {
    const w = engineButtonPreset(CATALOG, ALL);
    expect(w).toEqual({
      type: 'dualAction',
      label: 'ENGINE START STOP',
      tap: 'ignition-on',
      hold: 'starter',
      holdMode: 'fire',
      look: { mode: 'images', image: ENGINE },
      state: { lit: 'truck.electric.enabled', led: 'truck.engine.enabled' },
    });
    expect('look' in (engineButtonPreset(CATALOG, NONE) as object)).toBe(false);
  });

  it('returns null for a non-SCS catalog or a catalog without the actions', () => {
    const acc = { ...CATALOG, game: 'acc' as const };
    expect(headlightPreset(acc, ALL)).toBeNull();
    expect(wiperPreset(acc, ALL)).toBeNull();
    expect(engineButtonPreset(acc, ALL)).toBeNull();
    const bare = { ...CATALOG, actions: CATALOG.actions.filter((a) => a.id !== 'wipers-fast' && a.id !== 'starter') };
    expect(wiperPreset(bare, ALL)).toBeNull();
    expect(engineButtonPreset(bare, ALL)).toBeNull();
    expect(headlightPreset(bare, ALL)).not.toBeNull();
  });

  it('places every preset into a layout without any problem', () => {
    let p = profileWith(emptyLayout(8, 3));
    for (const draft of [headlightPreset(CATALOG, ALL), wiperPreset(CATALOG, ALL), engineButtonPreset(CATALOG, ALL)]) {
      const r = addWidget(p, IPHONE, draft!);
      if (!r.ok) {
        throw new Error(r.reason);
      }
      p = r.profile;
    }
    expect(layoutProblems(p.layouts[0]!, IPHONE, CATALOG, ALL)).toEqual([]);
    expect(p.layouts[0]?.widgets.map((w) => w.id)).toEqual(['lights', 'wipers', 'engine-start-stop']);
  });
});
