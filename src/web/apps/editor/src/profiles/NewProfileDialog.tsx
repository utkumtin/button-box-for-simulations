import { useState } from 'react';
import type { Game, Profile } from '@bbs/protocol';
import type { GameChoice } from '../lib/catalogView';
import { renameProfile } from '../lib/layoutOps';

// Only a carrier for `renameProfile`, which owns the name rules.
const BLANK: Profile = { schemaVersion: 1, id: 'new', name: '', game: 'ets2', layouts: [] };

/** New profile: a name and a game. Games without a shipped catalog are listed as "coming later" (decisions §4). */
export function NewProfileDialog({
  choices,
  busy,
  errors,
  onCreate,
  onCancel,
}: {
  choices: GameChoice[];
  busy: boolean;
  errors: string[];
  onCreate: (name: string, game: Game) => void;
  onCancel: () => void;
}) {
  const firstAvailable = choices.find((c) => c.available)?.game;
  const [name, setName] = useState('');
  const [game, setGame] = useState<Game | undefined>(firstAvailable);
  // The same rules as renaming: trimmed, 1 to 64 characters, no control characters.
  const check = renameProfile(BLANK, name);
  const canCreate = check.ok && game !== undefined && !busy;

  return (
    <div className="overlay">
      <form
        className="dialog pe-confirm"
        role="dialog"
        aria-modal="true"
        aria-labelledby="pe-new-title"
        onSubmit={(e) => {
          e.preventDefault();
          if (canCreate && check.ok) {
            onCreate(check.profile.name, game);
          }
        }}
      >
        <h3 id="pe-new-title">New profile</h3>
        <label className="pe-field">
          <span>Name</span>
          <input className="input" type="text" autoFocus maxLength={64} value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <fieldset className="pe-group">
          <legend>Game (cannot be changed later)</legend>
          {choices.map((c) => (
            <label key={c.game} className={c.available ? 'pe-check' : 'pe-check muted'}>
              <input type="radio" name="game" value={c.game} disabled={!c.available} checked={game === c.game} onChange={() => setGame(c.game)} />
              {c.name}
              {!c.available && ' (coming later)'}
            </label>
          ))}
        </fieldset>
        {errors.map((m, i) => (
          <p key={i} className="field-error" role="alert">
            {m}
          </p>
        ))}
        <div className="row-actions">
          <button type="button" className="btn" onClick={onCancel}>
            Cancel
          </button>
          <button type="submit" className="btn btn-primary" disabled={!canCreate}>
            Create
          </button>
        </div>
      </form>
    </div>
  );
}
