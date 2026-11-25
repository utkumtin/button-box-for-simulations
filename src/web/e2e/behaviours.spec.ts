import { expect, test, type Box } from './fixtures';
import { EditorApi, hostInfo, type E2EProfile } from './host';
import { readSeed, restoreSeed } from './seed';

// The Phase 5 widget behaviours (decisions section 5) from the phone to the Simulator and back: phone client in WebKit, the real
// WebSocket layer, the layout engine and the ETS2/ATS Simulator. Keyboard presses in the game come from the host's /game control.
// Every test leaves the Simulator and the seeded profile as it found them, so the other specs still start from a fresh host.
test.describe.configure({ mode: 'serial' });

const names = (list: { name: string }[]) => list.map((r) => r.name);

let seed: E2EProfile;
test.beforeAll(async () => {
  seed = await readSeed();
});
test.afterEach(async ({ control }) => {
  // Back to a parked truck with the engine off, whatever the test left running, then the seeded profile (a new engine, every knob at 0).
  await control.setState({ telemetry: true, gameListening: true, engineNeverStarts: false });
  const fields = await new EditorApi(hostInfo()).telemetry();
  await control.game('ignitionoff');
  await control.game('lightoff');
  await control.game('wipers0');
  if (fields['truck.light.beam.high'] === 1) await control.game('hblight');
  await restoreSeed(seed);
});

/** A drag of the knob from its centre to the given clockwise angle: one select, as a finger would do. */
async function turnKnob(box: Box, id: string, degrees: number): Promise<void> {
  const knob = await box.widget(id).locator('.bbs-knob-turn').boundingBox();
  if (!knob) throw new Error('Knob not visible.');
  const cx = knob.x + knob.width / 2;
  const cy = knob.y + knob.height / 2;
  const r = knob.width / 2;
  const angle = (degrees * Math.PI) / 180;
  const at = (f: number) => ({ x: cx + f * r * Math.sin(angle), y: cy - f * r * Math.cos(angle) });
  await box.touch([
    { type: 'down', id: 1, x: cx, y: cy },
    { type: 'move', id: 1, ...at(0.3), wait: 30 },
    { type: 'move', id: 1, ...at(0.8), wait: 30 },
    { type: 'up', id: 1, ...at(0.8) },
  ]);
}

/** A press held past the 500 ms hold time of the client (hold mode fire: the hold action goes out at 500 ms). */
async function holdButton(box: Box, id: string): Promise<void> {
  const { x, y } = await box.point(id, 0.5, 0.5);
  await box.touch([
    { type: 'down', id: 1, x, y, wait: 600 },
    { type: 'up', id: 1, x, y },
  ]);
}

/** Every `data-position` the widget shows from now on, in order, with the current one first (a mutation observer, so a flash is caught). */
async function watchPositions(box: Box, id: string): Promise<() => Promise<string[]>> {
  await box.page.evaluate((widgetId) => {
    const el = document.querySelector(`[data-widget-id="${widgetId}"]`);
    if (!el) throw new Error('No such widget.');
    const seen = [el.getAttribute('data-position') ?? ''];
    new MutationObserver(() => seen.push(el.getAttribute('data-position') ?? '')).observe(el, {
      attributes: true,
      attributeFilter: ['data-position'],
    });
    (window as unknown as { __seen: string[] }).__seen = seen;
  }, id);
  return () => box.page.evaluate(() => (window as unknown as { __seen: string[] }).__seen);
}

/** Starts the clock when the host has accepted a starter gesture: the received poll is the earliest the test can know. */
async function untilStarterSent(box: Box, count: number): Promise<number> {
  await expect.poll(async () => names(await box.received()).filter((n) => n === 'ignitionstrt').length).toBe(count);
  return Date.now();
}

test('engine button: tap switches the electrics on, hold starts, a tap while running does nothing, a hold stops', async ({ open }) => {
  const box = await open();
  const engine = box.widget('engine');
  await expect(engine).toHaveAttribute('data-lit', 'false');
  await expect(engine).toHaveAttribute('data-led', 'off');

  // Tap from all off: ignition on, the backlight follows the electrics.
  await box.tap('engine');
  await expect.poll(async () => names(await box.received())).toEqual(['ignitionon']);
  await expect(engine).toHaveAttribute('data-lit', 'true');
  await expect(engine).toHaveAttribute('data-led', 'off');

  // Hold with the electrics on: only the starter. The Simulator needs about 1.3 s, so the LED is still off right after the send.
  await holdButton(box, 'engine');
  await expect.poll(async () => names(await box.received())).toEqual(['ignitionon', 'ignitionstrt']);
  await expect(engine).toHaveAttribute('data-led', 'off', { timeout: 200 });
  await expect(engine).toHaveAttribute('data-led', 'green', { timeout: 4_000 });
  await expect(engine).toHaveAttribute('data-lit', 'true');

  // Tap while running: nothing goes out. Absence cannot be polled for, so give a stray output time to reach the Simulator.
  await box.tap('engine');
  await box.page.waitForTimeout(600);
  expect(names(await box.received())).toEqual(['ignitionon', 'ignitionstrt']);
  await expect(engine).toHaveAttribute('data-led', 'green');
  await expect(engine).toHaveAttribute('data-lit', 'true');

  // Hold while running: ignition off, the button goes dark.
  await holdButton(box, 'engine');
  await expect.poll(async () => names(await box.received())).toEqual(['ignitionon', 'ignitionstrt', 'ignitionoff']);
  await expect(engine).toHaveAttribute('data-lit', 'false');
  await expect(engine).toHaveAttribute('data-led', 'off');
});

test('engine button: a hold from all off sends ignitionon and then the starter', async ({ open }) => {
  const box = await open();
  await holdButton(box, 'engine');
  await expect.poll(async () => names(await box.received())).toEqual(['ignitionon', 'ignitionstrt']);
  await expect(box.widget('engine')).toHaveAttribute('data-led', 'green', { timeout: 4_000 });
});

test('engine button: a start that never completes turns amber after 4 s, the next hold clears it at once, a start then goes green', async ({
  open,
  control,
}) => {
  test.setTimeout(45_000);
  await control.setState({ engineNeverStarts: true });
  const box = await open();
  const engine = box.widget('engine');

  await holdButton(box, 'engine');
  const sent = await untilStarterSent(box, 1);
  // The 4 s run from the host's acceptance of the gesture, which is at most one poll before `sent`.
  await box.page.waitForTimeout(Math.max(0, sent + 2_800 - Date.now()));
  await expect(engine).toHaveAttribute('data-led', 'off', { timeout: 100 });
  await expect(engine).toHaveAttribute('data-led', 'amber', { timeout: 2_700 });
  expect(Date.now() - sent).toBeGreaterThan(3_000);
  await expect(engine).toHaveAttribute('data-lit', 'true');

  // The next hold is a new attempt: amber is gone before the next 4 s could be over.
  await holdButton(box, 'engine');
  await untilStarterSent(box, 2);
  await expect(engine).toHaveAttribute('data-led', 'off', { timeout: 1_500 });

  // The engine can start now: green, and no amber later.
  await control.setState({ engineNeverStarts: false });
  await holdButton(box, 'engine');
  await expect(engine).toHaveAttribute('data-led', 'green', { timeout: 4_000 });
});

test('engine button: electric off from the keyboard after a failed start clears the amber LED', async ({ open, control }) => {
  test.setTimeout(30_000);
  await control.setState({ engineNeverStarts: true });
  const box = await open();
  const engine = box.widget('engine');

  await holdButton(box, 'engine');
  await expect(engine).toHaveAttribute('data-led', 'amber', { timeout: 6_000 });
  await expect(engine).toHaveAttribute('data-lit', 'true');

  await control.game('ignitionoff');
  await expect(engine).toHaveAttribute('data-led', 'off');
  await expect(engine).toHaveAttribute('data-lit', 'false');
});

test('engine button: a start from the keyboard turns the LED green and a stop from the keyboard turns it off', async ({ open, control }) => {
  const box = await open();
  const engine = box.widget('engine');
  await control.game('ignitionon');
  await control.game('ignitionstrt');
  await expect(engine).toHaveAttribute('data-led', 'green', { timeout: 4_000 });
  await expect(engine).toHaveAttribute('data-lit', 'true');
  await control.game('ignitionoff');
  await expect(engine).toHaveAttribute('data-led', 'off');
  await expect(engine).toHaveAttribute('data-lit', 'false');
});

test('headlights: HIGH sends lighton and then the high beam without showing LOW; LOW turns it off; the keyboard moves the knob', async ({
  open,
  control,
}) => {
  const box = await open();
  const lights = box.widget('lights');
  await expect(lights).toHaveAttribute('data-position', '0');

  // OFF -> HIGH: the engine adds the toggle because the high beam is off. LOW must not show in between. (The Simulator answers both
  // outputs within microseconds and the host sends only the latest state, so a flash of LOW would be coalesced away here; the next
  // test holds the inference pause open long enough to see it.)
  const positions = await watchPositions(box, 'lights');
  await turnKnob(box, 'lights', 60);
  await expect.poll(async () => names(await box.received())).toEqual(['lighton', 'hblight']);
  await expect(lights).toHaveAttribute('data-position', '3');
  // Past the one-second inference pause the telemetry (low and high beam on) still reads HIGH.
  await box.page.waitForTimeout(1_300);
  await expect(lights).toHaveAttribute('data-position', '3');
  // Only OFF and HIGH ever show (the knob may fall back to OFF for one round trip after the drag ends: the client shows the
  // reported position again until the host's state with the new one arrives).
  expect(new Set(await positions())).toEqual(new Set(['0', '3']));
  expect((await new EditorApi(hostInfo()).telemetry())['truck.light.beam.high']).toBe(1);

  // HIGH -> LOW: lighton again and the toggle that turns the high beam off.
  const back = await watchPositions(box, 'lights');
  await turnKnob(box, 'lights', 30);
  await expect.poll(async () => names(await box.received())).toEqual(['lighton', 'hblight', 'lighton', 'hblight']);
  await expect(lights).toHaveAttribute('data-position', '2');
  await box.page.waitForTimeout(1_300);
  await expect(lights).toHaveAttribute('data-position', '2');
  expect(new Set(await back())).toEqual(new Set(['3', '2']));
  expect((await new EditorApi(hostInfo()).telemetry())['truck.light.beam.high']).toBe(0);

  // The light switch turned off on the keyboard: the knob follows the telemetry to OFF.
  await control.game('lightoff');
  await expect(lights).toHaveAttribute('data-position', '0');
});

test('headlights: AUTO sends lightpark, and the keyboard moving the lights to low beam moves the knob to LOW', async ({ open, control }) => {
  const box = await open();
  const lights = box.widget('lights');
  await turnKnob(box, 'lights', 0);
  await expect.poll(async () => names(await box.received())).toEqual(['lightpark']);
  await expect(lights).toHaveAttribute('data-position', '1');

  await control.game('lighton');
  await expect(lights).toHaveAttribute('data-position', '2');
});

test('headlights: after a turn of the knob the telemetry cannot move it for a second, then the game wins', async ({ open, control }) => {
  const box = await open();
  const lights = box.widget('lights');

  // Without telemetry HIGH sends only the light action (the high-beam state is unknown), so the game ends up on LOW while the user
  // asked for HIGH. That is a mismatch the engine must ride out for one second (decisions section 5.1).
  await control.setState({ telemetry: false });
  await turnKnob(box, 'lights', 60);
  await expect.poll(async () => names(await box.received())).toEqual(['lighton']);
  await expect(lights).toHaveAttribute('data-position', '3');
  await control.setState({ telemetry: true });
  await expect(lights).toHaveAttribute('data-position', '3');
  await box.page.waitForTimeout(300);
  await expect(lights).toHaveAttribute('data-position', '3');
  // Once the pause is over the knob shows what the game does: low beam on, high beam off.
  await expect(lights).toHaveAttribute('data-position', '2', { timeout: 3_000 });
  expect(names(await box.received())).toEqual(['lighton']);
});

test('wipers: LOW sends wipers2; wipers switched off and on from the keyboard move the knob to OFF, then AUTO', async ({ open, control }) => {
  const box = await open();
  const wipers = box.widget('wipers');
  await expect(wipers).toHaveAttribute('data-position', '0');

  await turnKnob(box, 'wipers', 30);
  await expect.poll(async () => names(await box.received())).toEqual(['wipers2']);
  await expect(wipers).toHaveAttribute('data-position', '2');
  expect((await new EditorApi(hostInfo()).telemetry())['truck.wipers']).toBe(1);
  // The wipers running does not move the knob: the flag cannot tell LOW from AUTO or FAST.
  await box.page.waitForTimeout(500);
  await expect(wipers).toHaveAttribute('data-position', '2');

  await control.game('wipers0');
  await expect(wipers).toHaveAttribute('data-position', '0');
  // The game's wiper key reaches AUTO first, so wipers turned on while the knob shows OFF show AUTO (decisions §5.3).
  await control.game('wipers1');
  await expect(wipers).toHaveAttribute('data-position', '1');
});

test('without telemetry the engine button is dim like the other state widgets, and it works again with telemetry', async ({ open, control }) => {
  const box = await open();
  await box.tap('engine');
  await expect(box.widget('engine')).toHaveAttribute('data-lit', 'true');
  await expect(box.widget('engine')).toHaveAttribute('data-dim', 'false');

  await control.setState({ telemetry: false });
  await expect(box.widget('engine')).toHaveAttribute('data-dim', 'true');
  await expect(box.widget('engine')).toHaveAttribute('data-lit', 'false');
  await expect(box.widget('engine')).toHaveAttribute('data-led', 'off');
  await expect(box.widget('beacon')).toHaveAttribute('data-dim', 'true');

  await control.setState({ telemetry: true });
  await expect(box.widget('engine')).toHaveAttribute('data-dim', 'false');
  await expect(box.widget('engine')).toHaveAttribute('data-lit', 'true');
});

test('the control route refuses unknown inputs, extra fields and malformed bodies', async ({ control }) => {
  const info = hostInfo();
  const post = (body: string, secret = info.secret) =>
    fetch(`http://127.0.0.1:${info.controlPort}/game`, {
      method: 'POST',
      headers: { 'X-E2E-Secret': secret, 'Content-Type': 'application/json' },
      body,
    });
  expect((await post('{"input":"no-such-input"}')).status).toBe(400);
  expect((await post('{"input":"lightoff","extra":1}')).status).toBe(400);
  expect((await post('{"input":7}')).status).toBe(400);
  expect((await post('{}')).status).toBe(400);
  expect((await post('[]')).status).toBe(400);
  expect((await post('not json')).status).toBe(400);
  expect((await post('{"input":"lightoff"}', 'wrong')).status).toBe(403);
  await expect(control.game('lightoff')).resolves.toBeUndefined();
});
