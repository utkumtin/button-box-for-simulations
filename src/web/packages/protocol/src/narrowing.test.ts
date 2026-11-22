import { describe, expect, it } from 'vitest';
import type { ClientMessage, HostMessage, Look, Output, Widget } from './index';

// Hand-written consumers of the generated types. `pnpm typecheck` fails when a schema change
// (renamed field, added or removed message or widget type) no longer fits them.
function assertNever(x: never): never {
  throw new Error(`unhandled: ${JSON.stringify(x)}`);
}

export function describeClient(msg: ClientMessage): string {
  switch (msg.type) {
    case 'hello':
      return msg.token;
    case 'gesture':
      return `${msg.widgetId}:${msg.gesture}`;
    case 'select':
      return `${msg.widgetId}:${msg.position}`;
    default:
      return assertNever(msg);
  }
}

export function describeHost(msg: HostMessage): string {
  switch (msg.type) {
    case 'welcome':
      return 'welcome';
    case 'layout':
      return msg.layout.templateId;
    case 'noLayout':
      return 'noLayout';
    case 'state':
      return Object.keys(msg.widgets).join();
    case 'status':
      return String(msg.gameListening && msg.telemetry);
    default:
      return assertNever(msg);
  }
}

export function describeWidget(w: Widget): string {
  switch (w.type) {
    case 'toggle':
      return w.action;
    case 'encoder':
      return w.minus + w.plus;
    case 'switch':
      return String(w.positions.length);
    case 'dualAction':
      return w.holdMode;
    default:
      return assertNever(w);
  }
}

export function describeLook(l: Look): string {
  switch (l.mode) {
    case 'icon':
      return l.icon;
    case 'images':
      return l.image + (l.on ?? '');
    default:
      return assertNever(l);
  }
}

export function describeOutput(o: Output): string {
  return o.kind === 'vjoy' ? String(o.button) : o.input;
}

// Compile-time only: each access is valid in exactly one union branch.
export function mustNotCompile(msg: ClientMessage, w: Widget): void {
  // @ts-expect-error token only exists on hello
  void msg.token;
  if (msg.type === 'gesture') {
    // @ts-expect-error position only exists on select
    void msg.position;
  }
  // @ts-expect-error minus is not on every widget
  void w.minus;
  if (w.type === 'toggle' && w.look) {
    // @ts-expect-error image only exists on the images look
    void w.look.image;
  }
  // @ts-expect-error a rotary only exists on a switch
  void w.rotary;
}

describe('generated protocol types', () => {
  it('narrow by the type discriminator', () => {
    expect(describeClient({ type: 'gesture', widgetId: 'w1', gesture: 'tap' })).toBe('w1:tap');
    expect(describeHost({ type: 'status', telemetry: true, gameListening: true })).toBe('true');
    expect(describeHost({ type: 'noLayout' })).toBe('noLayout');
    expect(describeLook({ mode: 'icon', icon: 'power' })).toBe('power');
  });
});
