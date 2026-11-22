import {
  mdiAlarmLight,
  mdiCarBrakeParking,
  mdiCarLightDimmed,
  mdiCarLightHigh,
  mdiEngine,
  mdiHazardLights,
  mdiMinus,
  mdiPlus,
  mdiPower,
  mdiWiper,
} from '@mdi/js';

/**
 * Icon registry (decisions section 7). Explicit named imports only, so unused MDI icons stay out of the bundle.
 * Keys are MDI names. Never add brand or logo icons.
 */
const ICONS: Readonly<Record<string, string>> = {
  power: mdiPower,
  'car-light-dimmed': mdiCarLightDimmed,
  'car-light-high': mdiCarLightHigh,
  'alarm-light': mdiAlarmLight,
  'hazard-lights': mdiHazardLights,
  'car-brake-parking': mdiCarBrakeParking,
  wiper: mdiWiper,
  engine: mdiEngine,
  minus: mdiMinus,
  plus: mdiPlus,
};

/** SVG path (24x24 viewBox) for a registry key, or undefined for an unknown key. */
export function iconPath(key: string): string | undefined {
  return Object.hasOwn(ICONS, key) ? ICONS[key] : undefined;
}

export const ICON_KEYS: readonly string[] = Object.keys(ICONS);
