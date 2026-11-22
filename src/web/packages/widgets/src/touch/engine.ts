import type { Layout, Widget } from '@bbs/protocol';
import { TOUCH, type GestureOut } from '../contract';
import { angleOf, cellRect, knobGeometry, nearestPosition, type Rect } from '../geometry';

/**
 * Pure touch engine (decisions section 3 and 5): turns pointer events into finished gestures.
 * No DOM, no React. Time comes from an injected timer API so tests can use a fake clock.
 */

export interface TimerApi {
  setTimer(ms: number, fn: () => void): unknown;
  clearTimer(handle: unknown): void;
}

export type Half = 'minus' | 'plus' | 'previous' | 'next';

/** What the UI should show right now. `null` when no finger owns a widget. */
export interface Visual {
  widgetId: string;
  /** Landing half for encoders and switches; null for toggles and dual-action buttons. */
  half: Half | null;
  /** Snapped knob preview while dragging a switch; null otherwise. */
  preview: number | null;
}

export interface TouchEngineOptions {
  timers: TimerApi;
  onGesture: (gesture: GestureOut) => void;
  onVisual: (visual: Visual | null) => void;
  /** The position a switch currently shows (state or default 0). */
  shownPosition: (widgetId: string) => number;
}

interface Hit {
  widget: Widget;
  rect: Rect;
}

interface Touch {
  pointerId: number;
  startX: number;
  startY: number;
  /** Beyond the drag threshold at some point; stays true. */
  moved: boolean;
  /** The action already fired (guard or hold); release does nothing. */
  fired: boolean;
  timer: unknown;
  hit: Hit | null;
  half: Half | null;
  dragging: boolean;
  preview: number | null;
}

type GestureName = Extract<GestureOut, { type: 'gesture' }>['gesture'];

export class TouchEngine {
  private hits: Hit[] = [];
  private layout: Layout | null = null;
  private width = 0;
  private height = 0;
  private enabled = true;
  private touch: Touch | null = null;

  constructor(private readonly opts: TouchEngineOptions) {}

  setLayout(layout: Layout): void {
    if (layout !== this.layout) this.cancelAll();
    this.layout = layout;
    this.computeHits();
  }

  setSize(width: number, height: number): void {
    this.width = width;
    this.height = height;
    this.computeHits();
  }

  /** Disabling cancels any touch in progress without firing. */
  setEnabled(enabled: boolean): void {
    this.enabled = enabled;
    if (!enabled) this.cancelAll();
  }

  /** True when this pointer now owns the grid. Coordinates are CSS px from the grid's top-left. */
  pointerDown(pointerId: number, x: number, y: number): boolean {
    if (!this.enabled || this.touch) return false;
    const hit = this.hitTest(x, y);
    const t: Touch = {
      pointerId,
      startX: x,
      startY: y,
      moved: false,
      fired: false,
      timer: undefined,
      hit,
      half: null,
      dragging: false,
      preview: null,
    };
    this.touch = t;
    if (hit) {
      const w = hit.widget;
      const left = x - hit.rect.x < hit.rect.w / 2;
      if (w.type === 'encoder') t.half = left ? 'minus' : 'plus';
      else if (w.type === 'switch') t.half = left ? 'previous' : 'next';
      this.armTimer(t, w);
      this.opts.onVisual({ widgetId: w.id, half: t.half, preview: null });
    }
    return true;
  }

  pointerMove(pointerId: number, x: number, y: number): void {
    const t = this.touch;
    if (!t || t.pointerId !== pointerId || !t.hit) return;
    const w = t.hit.widget;
    if (!t.moved && Math.hypot(x - t.startX, y - t.startY) > TOUCH.dragThresholdPx) {
      t.moved = true;
      this.clearTimer(t);
      if (w.type === 'switch') {
        t.dragging = true;
        t.preview = this.opts.shownPosition(w.id);
      }
    }
    if (t.dragging && w.type === 'switch') this.updatePreview(t, w, x, y);
  }

  pointerUp(pointerId: number): void {
    const t = this.touch;
    if (!t || t.pointerId !== pointerId) return;
    this.release(t);
    const hit = t.hit;
    if (!hit || t.fired) return;
    const w = hit.widget;
    switch (w.type) {
      case 'toggle':
        if (!t.moved && !w.longPressGuard) this.gesture(w.id, 'tap');
        break;
      case 'encoder':
        if (!t.moved && !w.longPressGuard && t.half) this.gesture(w.id, t.half);
        break;
      case 'dualAction':
        // The hold fires from the timer; a release before it (or any release, without a hold action) is a tap.
        if (!t.moved && w.tap) this.gesture(w.id, 'tap');
        break;
      case 'switch':
        if (t.dragging) {
          if (t.preview !== null && t.preview !== this.opts.shownPosition(w.id)) {
            this.opts.onGesture({ type: 'select', widgetId: w.id, position: t.preview });
          }
        } else if (!t.moved && !w.longPressGuard && t.half) {
          this.gesture(w.id, t.half);
        }
        break;
    }
  }

  /** pointercancel and lost capture: release without firing. */
  pointerCancel(pointerId: number): void {
    const t = this.touch;
    if (!t || t.pointerId !== pointerId) return;
    this.release(t);
  }

  cancelAll(): void {
    if (this.touch) this.release(this.touch);
  }

  private release(t: Touch): void {
    this.clearTimer(t);
    this.touch = null;
    if (t.hit) this.opts.onVisual(null);
  }

  private clearTimer(t: Touch): void {
    if (t.timer !== undefined) {
      this.opts.timers.clearTimer(t.timer);
      t.timer = undefined;
    }
  }

  /** Hold and long-press guard share one 0.5 s timer; it fires only if the finger has not moved beyond the threshold. */
  private armTimer(t: Touch, w: Widget): void {
    let action: (() => void) | null = null;
    if (w.type === 'dualAction') {
      if (w.hold) action = () => this.gesture(w.id, 'hold');
    } else if (w.longPressGuard) {
      if (w.type === 'toggle') action = () => this.gesture(w.id, 'tap');
      else if (t.half) {
        const half = t.half;
        action = () => this.gesture(w.id, half);
      }
    }
    if (!action) return;
    const fire = action;
    t.timer = this.opts.timers.setTimer(TOUCH.holdMs, () => {
      t.timer = undefined;
      if (this.touch !== t || t.moved) return;
      t.fired = true;
      fire();
    });
  }

  private updatePreview(t: Touch, w: Extract<Widget, { type: 'switch' }>, x: number, y: number): void {
    const hit = t.hit;
    if (!hit) return;
    const knob = knobGeometry(hit.rect.w, hit.rect.h, w.rotary);
    const dx = x - hit.rect.x - knob.cx;
    const dy = y - hit.rect.y - knob.cy;
    if (Math.hypot(dx, dy) < knob.radius * TOUCH.switchDeadZone) return;
    const preview = nearestPosition(
      w.positions.map((p) => p.angle),
      angleOf(dx, dy),
    );
    if (preview !== t.preview) {
      t.preview = preview;
      this.opts.onVisual({ widgetId: w.id, half: t.half, preview });
    }
  }

  private gesture(widgetId: string, gesture: GestureName): void {
    this.opts.onGesture({ type: 'gesture', widgetId, gesture });
  }

  private hitTest(x: number, y: number): Hit | null {
    for (let i = this.hits.length - 1; i >= 0; i--) {
      const h = this.hits[i];
      if (h && x >= h.rect.x && x < h.rect.x + h.rect.w && y >= h.rect.y && y < h.rect.y + h.rect.h) return h;
    }
    return null;
  }

  private computeHits(): void {
    const l = this.layout;
    this.hits = l
      ? l.widgets.map((widget) => ({
          widget,
          rect: cellRect(widget.cell, l.grid.columns, l.grid.rows, this.width, this.height),
        }))
      : [];
  }
}
