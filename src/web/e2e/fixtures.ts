import { expect, test as base, type Locator, type Page } from '@playwright/test';
import { Control, EditorApi, hostInfo, type DeviceRecord, type Received } from './host';

/** One scripted touch event. `wait` pauses (ms) after the event. */
export interface TouchStep {
  type: 'down' | 'move' | 'up' | 'cancel';
  id: number;
  x: number;
  y: number;
  wait?: number;
}

export interface Box {
  page: Page;
  /** Outputs received by the Simulator since the box was opened. */
  received: () => Promise<Received[]>;
  widget: (id: string) => Locator;
  /** A real WebKit touch tap at a point of a widget (fractions of its width and height). */
  tap: (id: string, fx?: number, fy?: number) => Promise<void>;
  /** Pointer events with pointerType "touch", dispatched on the grid in one go. Needed for hold, drag and a second finger. */
  touch: (steps: TouchStep[]) => Promise<void>;
  point: (id: string, fx: number, fy: number) => Promise<{ x: number; y: number }>;
}

interface Fixtures {
  control: Control;
  editorApi: EditorApi;
  /** The real editor in a desktop-size context with the session cookie. WebKit stands in for WebView2 (Chromium). */
  editor: () => Promise<Page>;
  open: (device?: 'phone' | 'noLayout') => Promise<Box>;
}

interface Violation {
  directive: string;
  blocked: string;
}

declare global {
  interface Window {
    __csp: Violation[];
    /** True once the "Reconnecting" overlay was in the DOM since the flag was last cleared. */
    __reconnecting: boolean;
    /** Set by a test on the loaded page; still there afterwards means the page was never reloaded. */
    __mark?: string;
  }
}

/** Runs in every page before its own scripts: collects CSP violations, so a test can fail on them. */
function watchCsp(): void {
  window.__csp = [];
  document.addEventListener('securitypolicyviolation', (e) => {
    window.__csp.push({ directive: e.violatedDirective, blocked: e.blockedURI });
  });
}

/**
 * A page in standalone mode with the device stored, like an installed home-screen app. Every test fails when a CSP violation
 * or an uncaught page error happened (decisions section 13).
 */
export const test = base.extend<Fixtures>({
  // eslint-disable-next-line no-empty-pattern
  control: async ({}, provide) => {
    const control = new Control(hostInfo());
    // Every test starts with a listening game and telemetry.
    await control.setState({ telemetry: true, gameListening: true, engineNeverStarts: false });
    await provide(control);
    await control.setState({ telemetry: true, gameListening: true, engineNeverStarts: false });
  },

  // eslint-disable-next-line no-empty-pattern
  editorApi: async ({}, provide) => {
    await provide(new EditorApi(hostInfo()));
  },

  editor: async ({ browser }, provide) => {
    const info = hostInfo();
    const context = await browser.newContext({ viewport: { width: 1400, height: 900 }, hasTouch: false, isMobile: false });
    // The user's own browser never has this cookie; the test takes it from the host's ready line.
    await context.addCookies([
      { name: 'bbs_editor', value: info.editorSecret, domain: '127.0.0.1', path: '/', httpOnly: true, sameSite: 'Strict' },
    ]);
    const problems: string[] = [];
    const state: { page: Page | null } = { page: null };

    await provide(async () => {
      const page = await context.newPage();
      page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`));
      // The host refusing an editor request (a 422 from the profile gate, a 401 from a missing cookie) is a failure too.
      page.on('response', (res) => {
        const url = new URL(res.url());
        if (url.pathname.startsWith('/editor/api/') && res.status() >= 400) {
          problems.push(`${res.request().method()} ${url.pathname} -> ${res.status()}`);
        }
      });
      await page.addInitScript(watchCsp);
      state.page = page;
      await page.goto(`http://127.0.0.1:${info.editorPort}/editor/`);
      return page;
    });

    if (state.page) {
      const csp = await state.page.evaluate(() => window.__csp).catch(() => [] as Violation[]);
      expect(csp, 'CSP violations').toEqual([]);
    }
    await context.close();
    expect(problems, 'page errors and refused editor requests').toEqual([]);
  },

  open: async ({ page, control }, provide) => {
    const errors: string[] = [];
    page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
    let opened = false;

    await provide(async (which = 'phone') => {
      const info = hostInfo();
      const device: DeviceRecord = info.devices[which];
      await page.addInitScript((record) => {
        Object.defineProperty(navigator, 'standalone', { value: true, configurable: true });
        window.__csp = [];
        document.addEventListener('securitypolicyviolation', (e) => {
          window.__csp.push({ directive: e.violatedDirective, blocked: e.blockedURI });
        });
        // A connection that stays up never shows the overlay; this catches one that flashes by between two polls.
        window.__reconnecting = false;
        new MutationObserver(() => {
          if (document.body?.textContent?.includes('Reconnecting')) window.__reconnecting = true;
        }).observe(document, { subtree: true, childList: true, characterData: true });
        try {
          localStorage.setItem('bbs.device', JSON.stringify(record));
        } catch {
          // The test fails later on a missing grid.
        }
      }, device);
      const baseline = (await control.received()).length;
      await page.goto(`https://127.0.0.1:${info.port}/`);
      opened = true;

      const widget = (id: string) => page.locator(`[data-widget-id="${id}"]`);
      const point = async (id: string, fx: number, fy: number) => {
        const box = await widget(id).boundingBox();
        if (!box) throw new Error(`Widget ${id} is not visible.`);
        return { x: box.x + box.width * fx, y: box.y + box.height * fy };
      };
      return {
        page,
        received: async () => (await control.received()).slice(baseline),
        widget,
        point,
        tap: async (id, fx = 0.5, fy = 0.5) => {
          const { x, y } = await point(id, fx, fy);
          await page.touchscreen.tap(x, y);
        },
        touch: (steps) =>
          page.evaluate(async (list) => {
            const grid = document.querySelector('.bbs-grid');
            if (!grid) throw new Error('No grid.');
            const names = { down: 'pointerdown', move: 'pointermove', up: 'pointerup', cancel: 'pointercancel' } as const;
            for (const s of list) {
              grid.dispatchEvent(
                new PointerEvent(names[s.type], {
                  pointerId: s.id,
                  pointerType: 'touch',
                  isPrimary: s.id === 1,
                  clientX: s.x,
                  clientY: s.y,
                  button: s.type === 'move' ? -1 : 0,
                  bubbles: true,
                  cancelable: true,
                }),
              );
              if (s.wait) await new Promise((r) => setTimeout(r, s.wait));
            }
          }, steps),
      };
    });

    if (opened) {
      const csp = await page.evaluate(() => window.__csp).catch(() => [] as Violation[]);
      expect(csp, 'CSP violations').toEqual([]);
      expect(errors, 'page errors').toEqual([]);
    }
  },
});

export { expect };
