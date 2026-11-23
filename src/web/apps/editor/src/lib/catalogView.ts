import type { Catalog, CatalogAction, CatalogList, Game } from '@bbs/protocol';

/** Helpers for the inspector and the game picker (decisions §4, §11). */

/** The name shown to the user: "Beacon", and on vJoy games "Pit limiter (vJoy 3)". */
export function actionLabel(action: CatalogAction): string {
  return action.output.kind === 'vjoy' ? `${action.name} (vJoy ${action.output.button})` : action.name;
}

export function findAction(catalog: Catalog, id: string | undefined): CatalogAction | undefined {
  return id === undefined ? undefined : catalog.actions.find((a) => a.id === id);
}

/** The state field the editor offers by default for an action (the user can still change it per widget). */
export function defaultStateField(catalog: Catalog, actionId: string | undefined): string | undefined {
  return findAction(catalog, actionId)?.stateField;
}

export const GAME_NAMES: Record<Game, string> = {
  ets2: 'Euro Truck Simulator 2',
  ats: 'American Truck Simulator',
  ac1: 'Assetto Corsa',
  acc: 'Assetto Corsa Competizione',
  acevo: 'Assetto Corsa EVO',
};

export interface GameChoice {
  game: Game;
  name: string;
  /** False: shown as "coming later" and cannot be chosen (decisions §4). */
  available: boolean;
}

/** All five games; only those with a shipped catalog can be chosen. */
export function gameChoices(shipped: CatalogList): GameChoice[] {
  const have = new Set(shipped.catalogs.map((c) => c.game));
  return (['ets2', 'ats', 'ac1', 'acc', 'acevo'] as const).map((game) => ({
    game,
    name: GAME_NAMES[game],
    available: have.has(game),
  }));
}
