import { describe, expect, it } from 'vitest';
import { MAX_IMAGE_BYTES, sniffImage } from './imageSniff';

const bytes = (...parts: (number[] | string)[]) =>
  new Uint8Array(parts.flatMap((p) => (typeof p === 'string' ? [...p].map((c) => c.charCodeAt(0)) : p)));
const PNG = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
const GENERIC = { ok: false, message: 'Only PNG and WebP images can be imported.' };

describe('sniffImage', () => {
  it('accepts a PNG by its signature', () => {
    expect(sniffImage(bytes(PNG, [0, 0, 0, 0]))).toEqual({ ok: true, type: 'image/png' });
  });

  it('accepts WebP: RIFF container with WEBP at offset 8', () => {
    expect(sniffImage(bytes('RIFF', [1, 2, 3, 4], 'WEBPVP8 '))).toEqual({ ok: true, type: 'image/webp' });
  });

  it('refuses RIFF that is not WebP (e.g. WAV or AVI)', () => {
    expect(sniffImage(bytes('RIFF', [1, 2, 3, 4], 'WAVEfmt '))).toEqual(GENERIC);
  });

  it('refuses SVG with a specific message, even with BOM and leading whitespace', () => {
    // Users rename .svg files or save XML with a BOM; the type is decided by content (decisions §7).
    const svg = bytes([0xef, 0xbb, 0xbf], '  \n\t<svg xmlns="http://www.w3.org/2000/svg"/>');
    expect(sniffImage(svg)).toEqual({ ok: false, message: 'SVG files cannot be imported; use PNG or WebP.' });
    expect(sniffImage(bytes('<?xml version="1.0"?><svg/>')).ok).toBe(false);
  });

  it('refuses GIF and JPEG with the generic message', () => {
    expect(sniffImage(bytes('GIF89a', [1, 2]))).toEqual(GENERIC);
    expect(sniffImage(bytes([0xff, 0xd8, 0xff, 0xe0, 0, 0]))).toEqual(GENERIC);
  });

  it('refuses an empty file', () => {
    expect(sniffImage(new Uint8Array())).toEqual(GENERIC);
  });

  it('accepts exactly 4 MiB and refuses one byte more', () => {
    const make = (n: number) => {
      const b = new Uint8Array(n);
      b.set(PNG);
      return b;
    };
    expect(sniffImage(make(MAX_IMAGE_BYTES)).ok).toBe(true);
    expect(sniffImage(make(MAX_IMAGE_BYTES + 1))).toEqual({ ok: false, message: 'The image is larger than 4 MB.' });
  });
});
