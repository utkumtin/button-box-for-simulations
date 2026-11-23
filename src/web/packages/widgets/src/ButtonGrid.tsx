import { useEffect, useLayoutEffect, useRef, useState, type PointerEvent } from 'react';
import type { Widget } from '@bbs/protocol';
import '@fontsource/barlow/latin-600.css';
import './styles.css';
import type { ButtonGridProps } from './contract';
import { cellRect } from './geometry';
import { TouchEngine, type Visual } from './touch/engine';
import { DualActionView, EncoderView, SwitchView, ToggleView, type WidgetViewProps } from './widgets';

function WidgetView(props: WidgetViewProps) {
  const { widget } = props;
  switch (widget.type) {
    case 'toggle':
      return <ToggleView {...props} widget={widget} />;
    case 'encoder':
      return <EncoderView {...props} widget={widget} />;
    case 'switch':
      return <SwitchView {...props} widget={widget} />;
    case 'dualAction':
      return <DualActionView {...props} widget={widget} />;
    default:
      return null;
  }
}

/**
 * Renders a layout as a button box and reports finished gestures. Owns the pointer listeners;
 * the recognition itself lives in the pure TouchEngine.
 */
export function ButtonGrid({ layout, states, dim, enabled, onGesture, assetUrl }: ButtonGridProps) {
  const ref = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 0, h: 0 });
  const [visual, setVisual] = useState<Visual | null>(null);
  const latest = useRef({ onGesture, states });
  useLayoutEffect(() => {
    latest.current = { onGesture, states };
  });

  const [engine] = useState(
    () =>
      new TouchEngine({
        timers: {
          setTimer: (ms, fn) => setTimeout(fn, ms),
          clearTimer: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
        },
        onGesture: (g) => latest.current.onGesture(g),
        onVisual: setVisual,
        shownPosition: (id) => latest.current.states[id]?.position ?? 0,
      }),
  );

  // Layout effects, not passive ones: they run before the browser paints the new grid, so a touch that starts on the new grid is
  // never judged against (and then cancelled by) the old layout. A passive effect runs after paint and swallowed a tap that came
  // right after a layout update (seen in the Phase 4 e2e run).
  useLayoutEffect(() => {
    engine.setLayout(layout);
  }, [engine, layout]);
  useLayoutEffect(() => {
    engine.setEnabled(enabled);
  }, [engine, enabled]);
  useEffect(() => () => engine.cancelAll(), [engine]);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => {
      const r = el.getBoundingClientRect();
      engine.setSize(r.width, r.height);
      setSize({ w: r.width, h: r.height });
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, [engine]);

  const local = (e: PointerEvent<HTMLDivElement>) => {
    const r = e.currentTarget.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top, r };
  };

  const onPointerDown = (e: PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return;
    const { x, y, r } = local(e);
    engine.setSize(r.width, r.height);
    if (!engine.pointerDown(e.pointerId, x, y)) return;
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      // Capture is best effort (the pointer may already be gone); the engine still gets up/cancel events.
    }
  };

  return (
    <div
      ref={ref}
      className="bbs-grid"
      onPointerDown={onPointerDown}
      onPointerMove={(e) => {
        const { x, y } = local(e);
        engine.pointerMove(e.pointerId, x, y);
      }}
      onPointerUp={(e) => engine.pointerUp(e.pointerId)}
      onPointerCancel={(e) => engine.pointerCancel(e.pointerId)}
      onLostPointerCapture={(e) => engine.pointerCancel(e.pointerId)}
      onContextMenu={(e) => e.preventDefault()}
    >
      {size.w > 0 && size.h > 0
        ? layout.widgets.map((widget: Widget) => (
            <WidgetView
              key={widget.id}
              widget={widget}
              rect={cellRect(widget.cell, layout.grid.columns, layout.grid.rows, size.w, size.h)}
              state={states[widget.id]}
              dim={dim}
              visual={visual?.widgetId === widget.id ? visual : null}
              assetUrl={assetUrl}
            />
          ))
        : null}
    </div>
  );
}
