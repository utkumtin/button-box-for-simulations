import type { ScreenInfo } from '@bbs/protocol';

// A plain address only: anything that could add mailto: parameters (?, &, #) or whitespace is refused.
const ADDRESS = /^[^\s@?&#%]+@[^\s@?&#%]+\.[^\s@?&#%]+$/;

/** The recipient from the build environment, or '' (a draft without recipient) when missing or malformed. */
export function templateRequestEmail(raw: unknown): string {
  if (typeof raw !== 'string') return '';
  const value = raw.trim();
  return ADDRESS.test(value) ? value : '';
}

// Set in apps/editor/.env (git-ignored) and inlined by Vite at build time (decisions §8).
export const TEMPLATE_REQUEST_EMAIL = templateRequestEmail(import.meta.env.VITE_TEMPLATE_REQUEST_EMAIL);

/** Prefilled mailto: draft asking for an official template. Only screen data is included; the user types the device name. */
export function buildTemplateRequestMailto(screen: ScreenInfo, recipient: string = TEMPLATE_REQUEST_EMAIL): string {
  const { width, height, safeArea: s, pixelRatio } = screen;
  const subject = 'Template request';
  const body = [
    'Device name: ',
    '',
    `Screen: ${width}x${height} CSS px`,
    `Safe area: top ${s.top}, right ${s.right}, bottom ${s.bottom}, left ${s.left}`,
    `Pixel ratio: ${pixelRatio}`,
  ].join('\r\n');
  return `mailto:${recipient}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;
}
