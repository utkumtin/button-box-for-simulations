import type { CatalogAction, CatalogList } from '@bbs/protocol';
import { describe, expect, it } from 'vitest';
import { actionLabel, defaultStateField, gameChoices } from './catalogView';
import { CATALOG } from './testFixtures';

describe('catalogView', () => {
  it('labels scs and vJoy actions', () => {
    const scs = CATALOG.actions[0] as CatalogAction;
    expect(actionLabel(scs)).toBe('Beacon');
    const vjoy: CatalogAction = { id: 'pit', name: 'Pit limiter', output: { kind: 'vjoy', button: 3 } };
    expect(actionLabel(vjoy)).toBe('Pit limiter (vJoy 3)');
  });

  it('offers the catalog stateField as the default', () => {
    expect(defaultStateField(CATALOG, 'beacon')).toBe('truck.light.beacon');
    expect(defaultStateField(CATALOG, 'hazard')).toBeUndefined();
    expect(defaultStateField(CATALOG, 'nope')).toBeUndefined();
    expect(defaultStateField(CATALOG, undefined)).toBeUndefined();
  });

  // Decisions §4: a game without a shipped catalog is "coming later" and cannot be chosen.
  it('marks only games with a shipped catalog as available', () => {
    const shipped: CatalogList = {
      catalogs: [
        { ...CATALOG, game: 'ets2' },
        { ...CATALOG, game: 'ats' },
      ],
    };
    const choices = gameChoices(shipped);
    expect(choices.filter((c) => c.available).map((c) => c.game)).toEqual(['ets2', 'ats']);
    expect(choices.filter((c) => !c.available).map((c) => c.name)).toEqual([
      'Assetto Corsa',
      'Assetto Corsa Competizione',
      'Assetto Corsa EVO',
    ]);
    expect(choices.find((c) => c.game === 'ets2')?.name).toBe('Euro Truck Simulator 2');
    expect(choices.find((c) => c.game === 'ats')?.name).toBe('American Truck Simulator');
  });

  it('makes a game available as soon as its catalog ships', () => {
    const choices = gameChoices({ catalogs: [{ ...CATALOG, game: 'acc' }] });
    expect(choices.find((c) => c.game === 'acc')?.available).toBe(true);
    expect(choices.find((c) => c.game === 'ets2')?.available).toBe(false);
  });
});
