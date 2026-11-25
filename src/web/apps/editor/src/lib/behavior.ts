import type { Game, Widget } from '@bbs/protocol';

/**
 * Widget behaviours (decisions §5, §5.1-5.3): presets whose actions and position count the host checks. The editor
 * locks them and flags a widget whose shape no longer matches, so Save is never refused by the host for this reason.
 * ETS2 and ATS only. Labels, angles, icons, look, rotary and the long-press guard stay editable.
 */

const HEADLIGHT_ACTIONS = ['light-off', 'light-park', 'light-on', 'light-on'] as const;
const WIPER_ACTIONS = ['wipers-off', 'wipers-auto', 'wipers-low', 'wipers-fast'] as const;

export const BEHAVIOR_ACTIONS = { headlights: HEADLIGHT_ACTIONS, wipers: WIPER_ACTIONS } as const;
export const ENGINE_TAP = 'ignition-on';
export const ENGINE_HOLD = 'starter';

/** True for the games whose profiles may use behaviours. */
export function behaviorGame(game: Game): boolean {
  return game === 'ets2' || game === 'ats';
}

/** The behaviour of a widget; toggles and encoders have none. */
export function behaviorOf(widget: Widget): string | undefined {
  return (widget as { behavior?: string }).behavior;
}

/** The short read-only line for the inspector, or null for a widget without a behaviour. */
export function behaviorLine(widget: Widget): string | null {
  const behavior = behaviorOf(widget);
  return behavior === undefined ? null : `Behaviour: ${behavior} (ETS2/ATS)`;
}

/**
 * Why `widget` does not have the shape its behaviour needs (same meaning as the host rule), or null. Widgets without a
 * behaviour are never flagged here.
 */
export function behaviorShapeProblem(widget: Widget, game: Game): string | null {
  const behavior = behaviorOf(widget);
  if (behavior === undefined) {
    return null;
  }
  if (!behaviorGame(game)) {
    return `uses the ${behavior} behaviour, which only ETS2 and ATS profiles can use.`;
  }
  if (widget.type === 'switch' && (behavior === 'headlights' || behavior === 'wipers')) {
    const wanted = BEHAVIOR_ACTIONS[behavior];
    if (widget.positions.length !== wanted.length) {
      return `uses the ${behavior} behaviour, which needs exactly ${wanted.length} positions.`;
    }
    if (widget.positions.some((p, i) => p.action !== wanted[i])) {
      return `uses the ${behavior} behaviour, which needs the actions ${wanted.join(', ')} in this order.`;
    }
    return null;
  }
  if (widget.type === 'dualAction' && behavior === 'engineStartStop') {
    if (widget.tap !== ENGINE_TAP || widget.hold !== ENGINE_HOLD || widget.holdMode !== 'fire') {
      return `uses the engineStartStop behaviour, which needs tap ${ENGINE_TAP} and hold ${ENGINE_HOLD} with hold mode fire.`;
    }
    if (widget.state !== undefined) {
      return 'uses the engineStartStop behaviour, which takes lit and LED from the engine, so it cannot have a state binding.';
    }
    return null;
  }
  return `uses the ${behavior} behaviour, which does not fit a ${widget.type} widget.`;
}

const LOCKED = 'The actions and positions of a behaviour widget are fixed. Add a plain switch for a custom one.';

/**
 * Why the edit from `before` to `after` is not allowed on a widget with a behaviour, or null. The behaviour cannot be
 * changed or removed; a switch keeps its position count and actions; an engine button keeps tap, hold and hold mode and
 * gets no state binding (an existing one may be removed).
 */
export function behaviorLockViolation(before: Widget, after: Widget): string | null {
  const was = behaviorOf(before);
  if (was === undefined) {
    return behaviorOf(after) === undefined ? null : 'A behaviour is set by the presets only.';
  }
  if (behaviorOf(after) !== was) {
    return 'The behaviour of this widget cannot be changed.';
  }
  if (before.type === 'switch' && after.type === 'switch') {
    if (
      after.positions.length !== before.positions.length ||
      after.positions.some((p, i) => p.action !== before.positions[i]?.action) ||
      after.cycle !== before.cycle
    ) {
      return LOCKED;
    }
  }
  if (before.type === 'dualAction' && after.type === 'dualAction') {
    if (after.tap !== before.tap || after.hold !== before.hold || after.holdMode !== before.holdMode) {
      return LOCKED;
    }
  }
  if (was === 'engineStartStop' && after.state !== undefined && JSON.stringify(after.state) !== JSON.stringify(before.state)) {
    return 'An engine start-stop button takes its lit and LED from the engine, so it has no state binding.';
  }
  return null;
}
