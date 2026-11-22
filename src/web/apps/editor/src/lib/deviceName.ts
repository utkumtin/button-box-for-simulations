export const DEVICE_NAME_MAX = 64;

// Control characters U+0000-U+001F and U+007F are rejected, as in the editor API schema.
// eslint-disable-next-line no-control-regex
const CONTROL = /[\u0000-\u001F\u007F]/;

/** Returns an error message, or null when the name is valid. Mirrors the schema: 1..64 characters, no control characters. */
export function validateDeviceName(name: string): string | null {
  if (name.length === 0) return 'The name cannot be empty.';
  if ([...name].length > DEVICE_NAME_MAX) return `The name can have at most ${DEVICE_NAME_MAX} characters.`;
  if (CONTROL.test(name)) return 'The name cannot contain control characters.';
  return null;
}
