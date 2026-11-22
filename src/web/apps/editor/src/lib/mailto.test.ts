import { describe, expect, it } from 'vitest';
import { buildTemplateRequestMailto, templateRequestEmail } from './mailto';

const screen = { width: 844, height: 390, safeArea: { top: 0, right: 47, bottom: 21, left: 47 }, pixelRatio: 3 };

function body(url: string): string {
  const q = url.slice(url.indexOf('?') + 1);
  return new URLSearchParams(q).get('body') ?? '';
}

describe('buildTemplateRequestMailto', () => {
  // The maintainers need exactly the screen data to build a template; missing a field makes the request useless.
  it('contains size, safe area and pixel ratio', () => {
    const b = body(buildTemplateRequestMailto(screen, 'a@b.c'));
    expect(b).toContain('844x390');
    expect(b).toContain('top 0, right 47, bottom 21, left 47');
    expect(b).toContain('Pixel ratio: 3');
  });

  // The user only types the device name; the draft must leave that line ready to fill in.
  it('leaves an empty "Device name:" line for the user', () => {
    expect(body(buildTemplateRequestMailto(screen))).toMatch(/^Device name: \r\n/);
  });

  // Raw spaces, newlines or ampersands would break the mailto URL.
  it('encodes subject and body with encodeURIComponent and works with an empty recipient', () => {
    const url = buildTemplateRequestMailto(screen, '');
    expect(url.startsWith('mailto:?subject=Template%20request&body=')).toBe(true);
    expect(url).not.toMatch(/[ \r\n]/);
    expect(url).toContain(encodeURIComponent('Device name: \r\n'));
  });

  // The recipient comes from a hand-edited .env file. A value with ?, & or # would inject its own mailto:
  // parameters (e.g. an extra cc), and a typo must not produce a broken link: both fall back to no recipient.
  it('accepts only a plain address from the environment', () => {
    expect(templateRequestEmail(' maint@example.org ')).toBe('maint@example.org');
    expect(templateRequestEmail(undefined)).toBe('');
    expect(templateRequestEmail('')).toBe('');
    expect(templateRequestEmail('not-an-address')).toBe('');
    expect(templateRequestEmail('a@b.c?cc=x@y.z')).toBe('');
    expect(templateRequestEmail('a@b.c&body=x')).toBe('');
    expect(templateRequestEmail('a b@c.d')).toBe('');
  });

  // No OS or browser version is collected (decisions §6).
  it('collects nothing beyond screen data', () => {
    expect(body(buildTemplateRequestMailto(screen))).not.toMatch(/iOS|Safari|version/i);
  });
});
