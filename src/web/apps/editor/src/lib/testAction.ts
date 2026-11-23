import type { Widget } from '@bbs/protocol';
import type { GestureOut } from '@bbs/widgets';

/**
 * Test mode (decisions §11): turns what the editor preview reports into the catalog action id to send, or null.
 * `ButtonGrid` already applies the long-press guard, so a reported gesture is mapped as it is.
 *
 * The preview has no game state, so a switch's position is kept here: editor-local, per switch id, starting at 0.
 * Previous/next stop at the ends (no wrap, nothing sent), like the phone (decisions §5).
 */
export type SwitchPositions = Readonly<Record<string, number>>;

export interface TestResult {
  actionId: string | null;
  positions: SwitchPositions;
}

const orNull = (id: string | undefined): string | null => (id ? id : null);

export function testAction(widget: Widget, gesture: GestureOut, positions: SwitchPositions): TestResult {
  const none: TestResult = { actionId: null, positions };
  if (gesture.widgetId !== widget.id) {
    return none;
  }

  if (gesture.type === 'select') {
    if (widget.type !== 'switch' || !Number.isInteger(gesture.position)) {
      return none;
    }
    const target = widget.positions[gesture.position];
    if (!target) {
      return none;
    }
    return { actionId: orNull(target.action), positions: { ...positions, [widget.id]: gesture.position } };
  }

  switch (widget.type) {
    case 'toggle':
      return gesture.gesture === 'tap' ? { actionId: orNull(widget.action), positions } : none;
    case 'encoder':
      if (gesture.gesture === 'minus') {
        return { actionId: orNull(widget.minus), positions };
      }
      return gesture.gesture === 'plus' ? { actionId: orNull(widget.plus), positions } : none;
    case 'dualAction':
      if (gesture.gesture === 'tap') {
        return { actionId: orNull(widget.tap), positions };
      }
      // Only `fire` exists today; starter takeover is not offered before its phase.
      return gesture.gesture === 'hold' && widget.holdMode === 'fire'
        ? { actionId: orNull(widget.hold), positions }
        : none;
    case 'switch': {
      if (gesture.gesture !== 'previous' && gesture.gesture !== 'next') {
        return none;
      }
      const current = positions[widget.id] ?? 0;
      const next = current + (gesture.gesture === 'next' ? 1 : -1);
      const target = widget.positions[next];
      if (!target) {
        return none;
      }
      return { actionId: orNull(target.action), positions: { ...positions, [widget.id]: next } };
    }
  }
}
