import { describe, expect, it } from 'vitest';
import type { Layout } from '@bbs/protocol';
import type { GestureOut } from '@bbs/widgets';
import { applyHostMessage, gestureFrame, initialHostModel, panelNote } from './hostModel';

const layoutA = { grid: { columns: 4, rows: 2 }, widgets: [{ id: 'a' }] } as unknown as Layout;
const layoutB = { grid: { columns: 2, rows: 2 }, widgets: [{ id: 'b' }] } as unknown as Layout;

describe('applyHostMessage', () => {
  it('layout replaces the previous one, because the host resends it on every reconnect', () => {
    const m1 = applyHostMessage(initialHostModel, { type: 'layout', layout: layoutA });
    const m2 = applyHostMessage(m1, { type: 'layout', layout: layoutB });
    expect(m2.layout).toBe(layoutB);
    expect(m2.noLayout).toBe(false);
  });

  it('noLayout drops the old layout so the phone never shows a layout the profile no longer has', () => {
    const m1 = applyHostMessage(initialHostModel, { type: 'layout', layout: layoutA });
    const m2 = applyHostMessage(m1, { type: 'noLayout' });
    expect(m2.layout).toBeNull();
    expect(m2.noLayout).toBe(true);
    expect(applyHostMessage(m2, { type: 'layout', layout: layoutA }).noLayout).toBe(false);
  });

  it('state replaces the whole map, so a widget missing from the new message loses its old state', () => {
    const m1 = applyHostMessage(initialHostModel, { type: 'state', widgets: { a: { lit: true }, b: { position: 2 } } });
    const m2 = applyHostMessage(m1, { type: 'state', widgets: { b: { position: 3 } } });
    expect(m2.states).toEqual({ b: { position: 3 } });
  });

  it('status updates only the status and leaves layout and state alone', () => {
    const m1 = applyHostMessage(initialHostModel, { type: 'layout', layout: layoutA });
    const m2 = applyHostMessage(m1, { type: 'state', widgets: { a: { lit: true } } });
    const m3 = applyHostMessage(m2, { type: 'status', telemetry: true, gameListening: false });
    expect(m3.status).toEqual({ telemetry: true, gameListening: false });
    expect(m3.layout).toBe(layoutA);
    expect(m3.states).toEqual({ a: { lit: true } });
  });

  it('ignores unknown and malformed messages, so a bad frame cannot blank the panel', () => {
    const m = applyHostMessage(initialHostModel, { type: 'layout', layout: layoutA });
    for (const bad of [
      null,
      42,
      'layout',
      [],
      {},
      { type: 'future' },
      { type: 'layout' },
      { type: 'layout', layout: 'x' },
      { type: 'layout', layout: { grid: {} } },
      { type: 'state', widgets: null },
      { type: 'state', widgets: [] },
      { type: 'status', telemetry: 'yes', gameListening: true },
      { type: 'status', telemetry: true },
    ]) {
      expect(applyHostMessage(m, bad)).toBe(m);
    }
  });
});

describe('panelNote', () => {
  it('shows nothing before the first status, so the panel is not dimmed on a guess', () => {
    expect(panelNote(null)).toEqual({ dim: false, label: null });
  });
  it('no telemetry wins over game not listening', () => {
    expect(panelNote({ telemetry: false, gameListening: false })).toEqual({ dim: true, label: 'No telemetry' });
    expect(panelNote({ telemetry: false, gameListening: true })).toEqual({ dim: true, label: 'No telemetry' });
  });
  it('game not listening dims with its own label', () => {
    expect(panelNote({ telemetry: true, gameListening: false })).toEqual({ dim: true, label: 'Game not listening' });
  });
  it('a healthy status is neither dim nor labelled', () => {
    expect(panelNote({ telemetry: true, gameListening: true })).toEqual({ dim: false, label: null });
  });
});

describe('gestureFrame', () => {
  const g: GestureOut = { type: 'gesture', widgetId: 'w1', gesture: 'tap' } as GestureOut;
  it('drops presses on a closed socket, because presses are never queued', () => {
    expect(gestureFrame(false, true, g)).toBeNull();
  });
  it('drops presses before welcome, because the host has not authenticated us yet', () => {
    expect(gestureFrame(true, false, g)).toBeNull();
  });
  it('sends the gesture exactly as the widgets reported it', () => {
    expect(gestureFrame(true, true, g)).toBe(JSON.stringify(g));
    const sel: GestureOut = { type: 'select', widgetId: 's', position: 3 };
    expect(JSON.parse(gestureFrame(true, true, sel) as string)).toEqual(sel);
  });
});
