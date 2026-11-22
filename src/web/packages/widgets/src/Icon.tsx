import { iconPath } from './icons';

/** Renders a registry icon as an SVG path. An unknown key renders nothing (the widget shows its label only). */
export function Icon({ name, className }: { name: string; className?: string }) {
  const d = iconPath(name);
  if (!d) return null;
  return (
    <svg className={className ?? 'bbs-icon'} viewBox="0 0 24 24" aria-hidden="true">
      <path d={d} fill="currentColor" />
    </svg>
  );
}
