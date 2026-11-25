import { describe, expect, it } from 'vitest';
import type { ScsPluginState } from '@bbs/protocol';
import { gameRow, installButton, installMessage, INSTALLED_TEXT, NOT_INCLUDED_TEXT, stateText } from './scsPlugin';

describe('SCS plugin panel texts', () => {
  it('every state has its text and only install-able states have a button', () => {
    const cases: [ScsPluginState, string, 'Install' | 'Update' | null][] = [
      ['gameNotFound', 'Game not found', null],
      ['notInstalled', 'Not installed', 'Install'],
      ['upToDate', 'Up to date', null],
      ['updateAvailable', 'Update available', 'Update'],
    ];
    for (const [state, text, button] of cases) {
      expect(stateText(state)).toBe(text);
      expect(installButton(state)).toBe(button);
    }
  });

  it('a row names the game, shows the folder when known and Connected', () => {
    expect(gameRow({ game: 'ets2', state: 'upToDate', installDir: 'C:\\Games\\ETS2', connected: true })).toEqual({
      game: 'ets2',
      name: 'Euro Truck Simulator 2',
      state: 'Up to date',
      installDir: 'C:\\Games\\ETS2',
      connected: true,
      button: null,
    });
    expect(gameRow({ game: 'ats', state: 'gameNotFound', connected: false })).toMatchObject({
      name: 'American Truck Simulator',
      installDir: null,
      connected: false,
    });
  });

  it('204 tells the user about the one-time SDK dialog', () => {
    expect(installMessage('ets2', 204)).toEqual({ ok: true, text: INSTALLED_TEXT });
    expect(INSTALLED_TEXT).toContain('advanced SDK features');
  });

  it('409 shows the host text (game running) and has a fallback', () => {
    expect(installMessage('ets2', 409, ['Close Euro Truck Simulator 2 first.'])).toEqual({
      ok: false,
      text: 'Close Euro Truck Simulator 2 first.',
    });
    expect(installMessage('ats', 409).text).toBe('Close American Truck Simulator first.');
  });

  it('422 shows every problem; 404, 503 and network errors have their own text', () => {
    expect(installMessage('ets2', 422, ['Could not write.', 'Verify failed.']).text).toBe('Could not write. Verify failed.');
    expect(installMessage('ets2', 422).text).toMatch(/could not be written or verified/);
    expect(installMessage('ets2', 404).text).toMatch(/not found/);
    expect(installMessage('ets2', 503).text).toBe(NOT_INCLUDED_TEXT);
    expect(installMessage('ets2', 0).text).toBe('Cannot reach the host.');
    expect(installMessage('ets2', 500)).toEqual({ ok: false, text: 'The install failed (500).' });
  });
});
