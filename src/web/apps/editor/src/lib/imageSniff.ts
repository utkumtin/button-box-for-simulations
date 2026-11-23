export const MAX_IMAGE_BYTES = 4 * 1024 * 1024;

export type ImageSniff =
  | { ok: true; type: 'image/png' | 'image/webp' }
  | { ok: false; message: string };

const PNG_SIGNATURE = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
const NOT_SUPPORTED = 'Only PNG and WebP images can be imported.';
const SVG_REFUSED = 'SVG files cannot be imported; use PNG or WebP.';
const TOO_BIG = 'The image is larger than 4 MB.';

function startsWith(bytes: Uint8Array, offset: number, expected: readonly number[]): boolean {
  if (bytes.length < offset + expected.length) return false;
  return expected.every((b, i) => bytes[offset + i] === b);
}

const ascii = (s: string) => [...s].map((c) => c.charCodeAt(0));

/** Text that starts like markup (after an optional BOM and whitespace) is treated as SVG/XML. */
function looksLikeMarkup(bytes: Uint8Array): boolean {
  let i = 0;
  if (startsWith(bytes, 0, [0xef, 0xbb, 0xbf])) i = 3;
  const limit = Math.min(bytes.length, 1024);
  while (i < limit && [0x20, 0x09, 0x0a, 0x0d].includes(bytes[i] ?? -1)) i++;
  return bytes[i] === 0x3c;
}

/** Decides the image type by content only; the file name and declared type are never used (decisions §7). */
export function sniffImage(bytes: Uint8Array): ImageSniff {
  let type: 'image/png' | 'image/webp' | undefined;
  if (startsWith(bytes, 0, PNG_SIGNATURE)) {
    type = 'image/png';
  } else if (startsWith(bytes, 0, ascii('RIFF')) && startsWith(bytes, 8, ascii('WEBP'))) {
    type = 'image/webp';
  }
  if (type === undefined) {
    return { ok: false, message: looksLikeMarkup(bytes) ? SVG_REFUSED : NOT_SUPPORTED };
  }
  if (bytes.length > MAX_IMAGE_BYTES) return { ok: false, message: TOO_BIG };
  return { ok: true, type };
}
