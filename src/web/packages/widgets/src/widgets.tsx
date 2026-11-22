import type { CSSProperties } from 'react';
import type { DualActionWidget, EncoderWidget, Look, SwitchWidget, ToggleWidget, Widget, WidgetState } from '@bbs/protocol';
import { Icon } from './Icon';
import { layerBox, squareSide, type Rect } from './geometry';
import type { Visual } from './touch/engine';

export interface WidgetViewProps<W extends Widget = Widget> {
  widget: W;
  rect: Rect;
  state: WidgetState | undefined;
  /** No telemetry: widgets with a state binding look dim and neutral. */
  dim: boolean;
  /** Set only while a finger owns this widget. */
  visual: Visual | null;
  assetUrl: (assetId: string) => string;
}

type Vars = CSSProperties & Record<`--${string}`, string>;

/** Absolute box from the grid cell; `--u` is 1 % of the smaller side, so content scales with the cell. */
function frame(rect: Rect): Vars {
  return {
    left: rect.x,
    top: rect.y,
    width: rect.w,
    height: rect.h,
    '--u': `${squareSide(rect.w, rect.h) / 100}px`,
  };
}

function classes(base: string, ...flags: [boolean, string][]): string {
  return [base, ...flags.filter(([on]) => on).map(([, name]) => name)].join(' ');
}

function isDimmed(widget: Widget, dim: boolean): boolean {
  return dim && widget.state !== undefined;
}

function LookView({ look, lit, assetUrl }: { look: Look | undefined; lit: boolean; assetUrl: (id: string) => string }) {
  if (!look) return null;
  if (look.mode === 'icon') return <Icon name={look.icon} />;
  // One image: the lit state is produced in code (CSS glow); an 'on' image replaces it when given.
  const id = lit && look.on ? look.on : look.image;
  return <img className="bbs-img" src={assetUrl(id)} alt="" draggable={false} />;
}

export function ToggleView({ widget, rect, state, dim, visual, assetUrl }: WidgetViewProps<ToggleWidget>) {
  const dimmed = isDimmed(widget, dim);
  const lit = !dimmed && state?.lit === true;
  return (
    <div
      className={classes('bbs-w bbs-toggle', [lit, 'is-lit'], [dimmed, 'is-dim'], [visual !== null, 'is-pressed'])}
      style={frame(rect)}
      data-widget-id={widget.id}
      data-lit={lit}
      data-dim={dimmed}
    >
      <div className="bbs-cap">
        <LookView look={widget.look} lit={lit} assetUrl={assetUrl} />
        {widget.label ? <span className="bbs-label">{widget.label}</span> : null}
      </div>
    </div>
  );
}

export function EncoderView({ widget, rect, dim, visual }: WidgetViewProps<EncoderWidget>) {
  const dimmed = isDimmed(widget, dim);
  return (
    <div
      className={classes('bbs-w bbs-encoder', [dimmed, 'is-dim'])}
      style={frame(rect)}
      data-widget-id={widget.id}
      data-dim={dimmed}
    >
      <div className="bbs-cap">
        <div className={classes('bbs-half', [visual?.half === 'minus', 'is-pressed'])}>
          <Icon name="minus" />
        </div>
        <div className={classes('bbs-half', [visual?.half === 'plus', 'is-pressed'])}>
          <Icon name="plus" />
        </div>
        {widget.label ? <span className="bbs-label bbs-encoder-label">{widget.label}</span> : null}
      </div>
    </div>
  );
}

export function DualActionView({ widget, rect, state, dim, visual, assetUrl }: WidgetViewProps<DualActionWidget>) {
  const dimmed = isDimmed(widget, dim);
  const lit = !dimmed && state?.lit === true;
  const led = dimmed ? 'off' : (state?.led ?? 'off');
  const side = squareSide(rect.w, rect.h);
  return (
    <div
      className={classes('bbs-w bbs-dual', [lit, 'is-lit'], [dimmed, 'is-dim'], [visual !== null, 'is-pressed'])}
      style={frame(rect)}
      data-widget-id={widget.id}
      data-lit={lit}
      data-led={led}
      data-dim={dimmed}
    >
      <div className="bbs-square" style={{ width: side, height: side }}>
        <div className={`bbs-led bbs-led-${led}`} />
        <div className="bbs-round">
          {widget.look?.mode === 'images' ? <LookView look={widget.look} lit={lit} assetUrl={assetUrl} /> : null}
          {widget.look?.mode === 'icon' ? <Icon name={widget.look.icon} /> : null}
          {widget.label ? <span className="bbs-label bbs-backlit">{widget.label}</span> : null}
        </div>
      </div>
    </div>
  );
}

export function SwitchView({ widget, rect, state, dim, visual, assetUrl }: WidgetViewProps<SwitchWidget>) {
  const dimmed = isDimmed(widget, dim);
  const side = squareSide(rect.w, rect.h);
  const reported = state?.position ?? 0;
  const shown = visual?.preview ?? (reported >= 0 && reported < widget.positions.length ? reported : 0);
  const angle = widget.positions[shown]?.angle ?? 0;
  const rotary = widget.rotary;
  const base = layerBox(side, rotary?.base);
  const knob = layerBox(side, rotary?.knob, 0.55);
  const highlight = layerBox(side, rotary?.highlight);
  const boxStyle = (b: { left: number; top: number; size: number }): CSSProperties => ({
    left: b.left,
    top: b.top,
    width: b.size,
    height: b.size,
  });
  // Position labels sit on the base face around the knob, upright (never rotated).
  const labelRadius = side * 0.4;
  return (
    <div
      className={classes('bbs-w bbs-switch', [dimmed, 'is-dim'], [visual !== null, 'is-pressed'])}
      style={frame(rect)}
      data-widget-id={widget.id}
      data-position={shown}
      data-dim={dimmed}
    >
      <div className="bbs-square" style={{ width: side, height: side }}>
        {rotary?.base ? (
          <img className="bbs-layer" style={boxStyle(base)} src={assetUrl(rotary.base.asset)} alt="" draggable={false} />
        ) : (
          <div className="bbs-layer bbs-base-code" style={boxStyle(base)} />
        )}
        {widget.positions.map((p, i) => {
          const rad = (p.angle * Math.PI) / 180;
          const pos: CSSProperties = {
            left: side / 2 + Math.sin(rad) * labelRadius,
            top: side / 2 - Math.cos(rad) * labelRadius,
          };
          return (
            <div key={i} className={classes('bbs-poslabel', [i === shown, 'is-current'])} style={pos}>
              {p.icon ? <Icon name={p.icon} /> : null}
              {p.label ? <span>{p.label}</span> : null}
            </div>
          );
        })}
        <div className="bbs-knob-turn" style={{ ...boxStyle(knob), transform: `rotate(${angle}deg)` }}>
          {rotary?.knob ? (
            <img className="bbs-fill" src={assetUrl(rotary.knob.asset)} alt="" draggable={false} />
          ) : (
            <div className="bbs-fill bbs-knob-code" />
          )}
        </div>
        {rotary?.highlight ? (
          <img className="bbs-layer" style={boxStyle(highlight)} src={assetUrl(rotary.highlight.asset)} alt="" draggable={false} />
        ) : (
          <div className="bbs-layer bbs-highlight-code" style={boxStyle(knob)} />
        )}
        {widget.label ? <span className="bbs-label bbs-switch-label">{widget.label}</span> : null}
      </div>
    </div>
  );
}
