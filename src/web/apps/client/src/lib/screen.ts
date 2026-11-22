import type { ScreenInfo } from '@bbs/protocol';

export interface ScreenInputs {
  innerWidth: number;
  innerHeight: number;
  safeArea: { top: number; right: number; bottom: number; left: number };
  devicePixelRatio: number;
}

/**
 * Builds the landscape ScreenInfo the host uses for the generic template.
 * Values are not clamped: the host rejects invalid ones.
 */
export function buildScreenInfo(input: ScreenInputs): ScreenInfo {
  const w = Math.round(input.innerWidth);
  const h = Math.round(input.innerHeight);
  return {
    width: Math.max(w, h),
    height: Math.min(w, h),
    safeArea: { ...input.safeArea },
    pixelRatio: input.devicePixelRatio,
  };
}

export function isPortrait(width: number, height: number): boolean {
  return height > width;
}
