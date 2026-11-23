import { useLayoutEffect, useRef, useState, type CSSProperties, type PointerEvent } from 'react';
import type { DeviceTemplate, GridCell, Layout, WidgetState } from '@bbs/protocol';
import { ButtonGrid, type GestureOut } from '@bbs/widgets';
import {
  HANDLES,
  cellBox,
  cellPixels,
  cutoutBox,
  frameScale,
  moveTarget,
  resizeTarget,
  safeAreaBox,
  sameCell,
  type Handle,
} from './canvasGeometry';

export interface CanvasProps {
  template: DeviceTemplate;
  layout: Layout;
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  /** Problem messages per widget id; a widget with messages is outlined in red. */
  problems: ReadonlyMap<string, string[]>;
  testMode: boolean;
  states: Readonly<Record<string, WidgetState>>;
  dim: boolean;
  onGesture: (gesture: GestureOut) => void;
  assetUrl: (assetId: string) => string;
  /** Called once on drop. The edit is refused or accepted by the caller, which shows the reason. */
  onMove: (widgetId: string, cell: GridCell) => void;
  onResize: (widgetId: string, cell: GridCell) => void;
}

interface Drag {
  pointerId: number;
  widgetId: string;
  handle: Handle | null;
  startX: number;
  startY: number;
  origin: GridCell;
}

const px = (n: number): string => `${n}px`;

/**
 * The device frame with the layout in it (decisions §11). The preview is the shared `ButtonGrid`; above it sits an
 * overlay for selecting, dragging and resizing with plain pointer events. In test mode the overlay is gone and the
 * grid takes the touches.
 */
export function Canvas(props: CanvasProps) {
  const { template, layout, selectedId, onSelect, problems, testMode } = props;
  const wrap = useRef<HTMLDivElement>(null);
  const [available, setAvailable] = useState(0);
  const drag = useRef<Drag | null>(null);
  const [preview, setPreview] = useState<{ widgetId: string; cell: GridCell } | null>(null);

  useLayoutEffect(() => {
    const el = wrap.current;
    if (!el) {
      return;
    }
    const measure = () => setAvailable(el.clientWidth);
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const frame = frameScale(template.screen, available);
  const safe = safeAreaBox(template.screen, frame.scale);
  const area = { width: safe.width, height: safe.height };
  const cutout = cutoutBox(template.cutout, template.screen, frame.scale);
  const cell = cellPixels(layout.grid, area);

  const targetFor = (d: Drag, e: PointerEvent): GridCell => {
    const dx = e.clientX - d.startX;
    const dy = e.clientY - d.startY;
    return d.handle
      ? resizeTarget(d.origin, d.handle, dx, dy, layout.grid, area)
      : moveTarget(d.origin, dx, dy, layout.grid, area);
  };

  const begin = (e: PointerEvent, widgetId: string, origin: GridCell, handle: Handle | null) => {
    if (e.button !== 0) {
      return;
    }
    e.stopPropagation();
    onSelect(widgetId);
    drag.current = { pointerId: e.pointerId, widgetId, handle, startX: e.clientX, startY: e.clientY, origin };
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      // Capture is best effort; the overlay still gets the move and up events while the pointer is over it.
    }
  };

  const onMove = (e: PointerEvent) => {
    const d = drag.current;
    if (d && d.pointerId === e.pointerId) {
      const next = targetFor(d, e);
      setPreview((old) => (old && old.widgetId === d.widgetId && sameCell(old.cell, next) ? old : { widgetId: d.widgetId, cell: next }));
    }
  };

  const finish = (e: PointerEvent, commit: boolean) => {
    const d = drag.current;
    if (!d || d.pointerId !== e.pointerId) {
      return;
    }
    drag.current = null;
    setPreview(null);
    if (!commit) {
      return;
    }
    const next = targetFor(d, e);
    if (!sameCell(next, d.origin)) {
      (d.handle ? props.onResize : props.onMove)(d.widgetId, next);
    }
  };

  const gridLines: CSSProperties = {
    backgroundSize: `${cell.width}px ${cell.height}px`,
  };

  return (
    <div ref={wrap} className="pe-canvas-wrap">
      {frame.scale > 0 && (
        <div className="pe-frame" style={{ width: px(frame.width), height: px(frame.height) }}>
          {cutout && (
            <div
              className={template.cutout === 'notch' ? 'pe-cutout pe-notch' : 'pe-cutout'}
              aria-hidden="true"
              style={{ left: px(cutout.x), top: px(cutout.y), width: px(cutout.width), height: px(cutout.height) }}
            />
          )}
          <div className="pe-safe" style={{ left: px(safe.x), top: px(safe.y), width: px(safe.width), height: px(safe.height) }}>
            <div className="pe-grid-host">
              <ButtonGrid
                layout={layout}
                states={props.states}
                dim={props.dim}
                enabled={testMode}
                onGesture={props.onGesture}
                assetUrl={props.assetUrl}
              />
            </div>
            {!testMode && (
              <div
                className="pe-overlay"
                style={gridLines}
                data-testid="pe-overlay"
                onPointerDown={(e) => {
                  if (e.target === e.currentTarget) {
                    onSelect(null);
                  }
                }}
                onPointerMove={onMove}
                onPointerUp={(e) => finish(e, true)}
                onPointerCancel={(e) => finish(e, false)}
              >
                {layout.widgets.map((w) => {
                  const shown = preview?.widgetId === w.id ? preview.cell : w.cell;
                  const box = cellBox(shown, layout.grid, area);
                  const messages = problems.get(w.id);
                  const classes = ['pe-box'];
                  if (w.id === selectedId) {
                    classes.push('is-selected');
                  }
                  if (messages && messages.length > 0) {
                    classes.push('has-problem');
                  }
                  if (preview?.widgetId === w.id) {
                    classes.push('is-dragging');
                  }
                  return (
                    <div
                      key={w.id}
                      className={classes.join(' ')}
                      style={{ left: px(box.x), top: px(box.y), width: px(box.width), height: px(box.height) }}
                      title={messages?.join('\n') ?? w.label ?? w.id}
                      aria-label={`${w.label ?? w.id}${messages?.length ? ' (has problems)' : ''}`}
                      onPointerDown={(e) => begin(e, w.id, w.cell, null)}
                    >
                      {w.id === selectedId &&
                        HANDLES.map((h) => (
                          <span key={h} className={`pe-handle pe-handle-${h}`} onPointerDown={(e) => begin(e, w.id, w.cell, h)} />
                        ))}
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
