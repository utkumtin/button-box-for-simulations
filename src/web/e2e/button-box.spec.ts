import { expect, test } from './fixtures';

const LABELS = ['ENGINE START STOP', 'LIGHTS', 'WIPERS', 'HIGH BEAM', 'BEACON', 'HAZARD', 'PARKING BRAKE', 'RETARDER'];
const WIDGETS = ['engine', 'lights', 'wipers', 'high-beam', 'beacon', 'hazard', 'parking-brake', 'retarder'];

const names = (list: { name: string }[]) => list.map((r) => r.name);

// The host keeps one game state for the whole run (engine, switch positions). The tests run in this order and each one
// uses its own widgets, so none depends on another's result, only on a fresh host.
test.describe.configure({ mode: 'serial' });

test('shows the demo layout with all widgets, labels and images, without CSP violations', async ({ open }) => {
  const box = await open();
  for (const id of WIDGETS) await expect(box.widget(id)).toBeVisible();
  for (const label of LABELS) await expect(box.page.getByText(label, { exact: true }).first()).toBeVisible();

  // The engine button and both rotary switches use images from /assets/<sha256>.
  const images = box.page.locator('.bbs-grid img');
  await expect.poll(() => images.count()).toBeGreaterThanOrEqual(5);
  const sources = await images.evaluateAll((list) => list.map((i) => i.getAttribute('src')));
  for (const src of sources) expect(src).toMatch(/^\/assets\/[0-9a-f]{64}$/);
  await expect
    .poll(() => images.evaluateAll((list) => list.every((i) => (i as HTMLImageElement).complete && (i as HTMLImageElement).naturalWidth > 0)))
    .toBe(true);
});

test('tapping the beacon sends beacon and lights the widget, a second tap unlights it', async ({ open }) => {
  const box = await open();
  const beacon = box.widget('beacon');
  await expect(beacon).toHaveAttribute('data-lit', 'false');

  await box.tap('beacon');
  await expect.poll(async () => names(await box.received())).toEqual(['beacon']);
  await expect(beacon).toHaveAttribute('data-lit', 'true');

  await box.tap('beacon');
  await expect.poll(async () => names(await box.received())).toEqual(['beacon', 'beacon']);
  await expect(beacon).toHaveAttribute('data-lit', 'false');
});

test('the encoder sends retarderdown on the left half and retarderup on the right half', async ({ open }) => {
  const box = await open();
  await box.tap('retarder', 0.25);
  await expect.poll(async () => names(await box.received())).toEqual(['retarderdown']);
  await box.tap('retarder', 0.75);
  await expect.poll(async () => names(await box.received())).toEqual(['retarderdown', 'retarderup']);
});

test('the wipers switch steps on a tap and jumps on a drag to the FAST position', async ({ open }) => {
  const box = await open();
  const wipers = box.widget('wipers');
  await expect(wipers).toHaveAttribute('data-position', '0');

  await box.tap('wipers', 0.75);
  await expect.poll(async () => names(await box.received())).toEqual(['wipers1']);
  await expect(wipers).toHaveAttribute('data-position', '1');

  // Drag the knob clockwise from its centre to the 60 degree position (FAST); one select, one output.
  const knob = await wipers.locator('.bbs-knob-turn').boundingBox();
  if (!knob) throw new Error('Knob not visible.');
  const cx = knob.x + knob.width / 2;
  const cy = knob.y + knob.height / 2;
  const r = knob.width / 2;
  const angle = (60 * Math.PI) / 180;
  const at = (f: number) => ({ x: cx + f * r * Math.sin(angle), y: cy - f * r * Math.cos(angle) });
  await box.touch([
    { type: 'down', id: 1, x: cx, y: cy },
    { type: 'move', id: 1, ...at(0.3), wait: 30 },
    { type: 'move', id: 1, ...at(0.8), wait: 30 },
    { type: 'up', id: 1, ...at(0.8) },
  ]);
  await expect.poll(async () => names(await box.received())).toEqual(['wipers1', 'wipers3']);
  await expect(wipers).toHaveAttribute('data-position', '3');
});

test('the engine button ignites on a tap and starts on a hold; the LED turns green', async ({ open }) => {
  const box = await open();
  const engine = box.widget('engine');
  await expect(engine).toHaveAttribute('data-lit', 'false');

  await box.tap('engine');
  await expect.poll(async () => names(await box.received())).toEqual(['ignitionon']);
  await expect(engine).toHaveAttribute('data-lit', 'true');
  await expect(engine).toHaveAttribute('data-led', 'off');

  const { x, y } = await box.point('engine', 0.5, 0.5);
  await box.touch([
    { type: 'down', id: 1, x, y, wait: 600 },
    { type: 'up', id: 1, x, y },
  ]);
  await expect.poll(async () => names(await box.received())).toContain('ignitionstrt');
  await expect(engine).toHaveAttribute('data-led', 'green', { timeout: 3_000 });
});

test('only one finger counts: a second pointer on another widget does nothing', async ({ open }) => {
  const box = await open();
  const held = await box.point('hazard', 0.5, 0.5);
  const other = await box.point('beacon', 0.5, 0.5);
  // Finger 1 rests on the hazard toggle and is cancelled (fires nothing); finger 2 taps the beacon meanwhile.
  await box.touch([
    { type: 'down', id: 1, x: held.x, y: held.y },
    { type: 'down', id: 2, x: other.x, y: other.y, wait: 20 },
    { type: 'up', id: 2, x: other.x, y: other.y, wait: 20 },
    { type: 'cancel', id: 1, x: held.x, y: held.y },
  ]);
  // Absence cannot be polled for: give a stray gesture time to reach the host.
  await box.page.waitForTimeout(500);
  expect(names(await box.received())).toEqual([]);
  await expect(box.widget('beacon')).toHaveAttribute('data-lit', 'false');
  // The engine is free again: one real tap gives exactly one output.
  await box.tap('beacon');
  await expect.poll(async () => names(await box.received())).toEqual(['beacon']);
  await expect(box.widget('beacon')).toHaveAttribute('data-lit', 'true');
  await box.tap('beacon'); // leave it unlit
  await expect.poll(async () => names(await box.received())).toEqual(['beacon', 'beacon']);
});

test('no telemetry dims the widgets with a label; a game that is not listening ignores presses', async ({ open, control }) => {
  const box = await open();
  const beacon = box.widget('beacon');
  await expect(beacon).toHaveAttribute('data-dim', 'false');
  await expect(box.page.getByText('No telemetry')).toHaveCount(0);

  await control.setState({ telemetry: false });
  await expect(box.page.getByText('No telemetry')).toBeVisible();
  await expect(beacon).toHaveAttribute('data-dim', 'true');
  await expect(beacon).toHaveClass(/is-dim/);

  await control.setState({ telemetry: true, gameListening: false });
  await expect(box.page.getByText('Game not listening')).toBeVisible();
  await expect(box.page.getByText('No telemetry')).toHaveCount(0);
  await box.tap('beacon');
  // Absence cannot be polled for: give the gesture time to reach the host, which drops it (no output), before the game listens again.
  await box.page.waitForTimeout(500);
  expect(names(await box.received())).toEqual([]);
  await control.setState({ gameListening: true });
  await expect(box.page.getByText('Game not listening')).toHaveCount(0);
  await expect(beacon).toHaveAttribute('data-lit', 'false');

  // A tap while the game listens does act, so the dropped one really sent nothing.
  await box.tap('beacon');
  await expect(beacon).toHaveAttribute('data-lit', 'true');
  await expect.poll(async () => names(await box.received())).toEqual(['beacon']);
  await box.tap('beacon');
  await expect(beacon).toHaveAttribute('data-lit', 'false');
});

test('dropping the connection shows "Reconnecting..." and the grid works again afterwards', async ({ open, control }) => {
  const box = await open();
  await expect(box.widget('hazard')).toBeVisible();
  await expect.poll(async () => (await control.state()).connected).toBe(1);

  await control.drop();
  const overlay = box.page.getByRole('status').filter({ hasText: 'Reconnecting' });
  await expect(overlay).toBeVisible();
  await expect(overlay).toHaveCount(0);

  await box.tap('hazard');
  await expect.poll(async () => names(await box.received())).toEqual(['flasher4way']);
  await expect(box.widget('hazard')).toHaveAttribute('data-lit', 'true');
  await box.tap('hazard');
  await expect.poll(async () => names(await box.received())).toEqual(['flasher4way', 'flasher4way']);
});

test('a device without a layout is told so', async ({ open }) => {
  const box = await open('noLayout');
  await expect(box.page.getByText('No layout for this device in the active profile.')).toBeVisible();
  await expect(box.page.locator('.bbs-grid')).toHaveCount(0);
});
