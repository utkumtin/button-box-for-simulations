import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

// The host serves these pages with `script-src 'self'; style-src 'self'` and no CDN.
// Any inline code or external URL in the built HTML would be blocked or violate that rule.
function read(app: 'client' | 'editor'): string {
  const path = fileURLToPath(new URL(`../apps/${app}/dist/index.html`, import.meta.url));
  if (!existsSync(path)) {
    throw new Error(`${path} is missing. Run "pnpm build" first (the "pnpm test" script does this).`);
  }
  return readFileSync(path, 'utf8');
}

function assetRefs(html: string, dir: string): string[] {
  return [...html.matchAll(/\b(?:src|href)="([^"]+)"/g)]
    .map((m) => m[1] ?? '')
    .filter((r) => r.includes(`${dir}/`));
}

describe.each(['client', 'editor'] as const)('%s dist/index.html respects the strict CSP', (app) => {
  const html = read(app);

  it('has no inline script', () => {
    const inline = [...html.matchAll(/<script\b([^>]*)>/gi)].filter(
      (m) => !/\bsrc\s*=/i.test(m[1] ?? ''),
    );
    expect(inline).toEqual([]);
  });

  it('has no inline style element or style attribute', () => {
    expect(html).not.toMatch(/<style\b/i);
    expect(html).not.toMatch(/\sstyle\s*=/i);
  });

  it('has no external URL', () => {
    expect(html).not.toMatch(/https?:\/\//i);
  });

  it('loads a script file and a stylesheet file', () => {
    expect(html).toMatch(/<script\b[^>]*\bsrc=/i);
    expect(html).toMatch(/<link\b[^>]*rel="stylesheet"/i);
  });
});

describe('asset base paths', () => {
  it('editor references assets under /editor/', () => {
    const assets = assetRefs(read('editor'), 'assets');
    expect(assets.length).toBeGreaterThan(0);
    for (const r of assets) expect(r).toMatch(/^\/editor\/assets\//);
  });

  // /assets/ on the LAN server is the image route (decisions §7); the client bundle must not live there.
  it('client references its bundle under /static/', () => {
    const assets = assetRefs(read('client'), 'static');
    expect(assets.length).toBeGreaterThan(0);
    for (const r of assets) expect(r).toMatch(/^\/static\//);
    expect(read('client')).not.toMatch(/="\/assets\//);
  });
});
