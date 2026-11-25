import type { ScsGame, ScsGameStatus, ScsPluginState } from '@bbs/protocol';

/**
 * Texts of the Settings "SCS plugin" panel (decisions §11 Phase 5, §12): state to text and button, install answer to
 * message. Pure so the wording is tested; the panel only shows it.
 */

export const SCS_GAME_NAMES: Record<ScsGame, string> = {
  ets2: 'Euro Truck Simulator 2',
  ats: 'American Truck Simulator',
};

export const NOT_INCLUDED_TEXT = 'The plugin is not included in this build.';

export const INSTALLED_TEXT =
  'Installed. The game asks once to allow advanced SDK features the next time it starts – choose OK.';

const STATE_TEXT: Record<ScsPluginState, string> = {
  gameNotFound: 'Game not found',
  notInstalled: 'Not installed',
  upToDate: 'Up to date',
  updateAvailable: 'Update available',
};

export function stateText(state: ScsPluginState): string {
  return STATE_TEXT[state];
}

/** The button for a state: Install, Update, or none (nothing to do, or no game to install into). */
export function installButton(state: ScsPluginState): 'Install' | 'Update' | null {
  if (state === 'notInstalled') {
    return 'Install';
  }
  return state === 'updateAvailable' ? 'Update' : null;
}

export interface GameRow {
  game: ScsGame;
  name: string;
  state: string;
  installDir: string | null;
  connected: boolean;
  button: 'Install' | 'Update' | null;
}

export function gameRow(status: ScsGameStatus): GameRow {
  return {
    game: status.game,
    name: SCS_GAME_NAMES[status.game],
    state: stateText(status.state),
    installDir: status.installDir ?? null,
    connected: status.connected,
    button: installButton(status.state),
  };
}

export interface InstallMessage {
  ok: boolean;
  text: string;
}

/**
 * The message for an install answer: `status` is 204, the failing HTTP status, or 0 when the host cannot be reached; `problems` the sentences of a 409 or
 * 422 `ProblemList` (the host names the game that must be closed, so they are shown as they are).
 */
export function installMessage(game: ScsGame, status: number, problems: readonly string[] = []): InstallMessage {
  if (status === 204) {
    return { ok: true, text: INSTALLED_TEXT };
  }
  const name = SCS_GAME_NAMES[game];
  if (status === 0) {
    return { ok: false, text: 'Cannot reach the host.' };
  }
  if (status === 404) {
    return { ok: false, text: `${name} was not found, so the plugin cannot be installed.` };
  }
  if (status === 409) {
    return { ok: false, text: problems.length > 0 ? problems.join(' ') : `Close ${name} first.` };
  }
  if (status === 503) {
    return { ok: false, text: NOT_INCLUDED_TEXT };
  }
  if (status === 422) {
    return {
      ok: false,
      text: problems.length > 0 ? problems.join(' ') : 'The plugin could not be written or verified. Nothing was changed.',
    };
  }
  return { ok: false, text: `The install failed (${status}).` };
}
