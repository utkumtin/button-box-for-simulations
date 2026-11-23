import { expect, test } from './fixtures';
import type { E2EProfile } from './host';
import { phoneWidget, readSeed, restoreSeed } from './seed';

// The real editor bundle served by the host's loopback server, with the session cookie, under the real CSP. Chrome is not
// installed on the CI runners, so WebKit stands in for WebView2 (Chromium): it checks the page and the CSP, not the engine.
// The editor runs in its own desktop-size context (1400 x 900, no touch); the phone uses the project's iPhone context.
test.describe.configure({ mode: 'serial' });

let seed: E2EProfile;
test.beforeAll(async () => {
  seed = await readSeed();
});
test.afterEach(async () => {
  await restoreSeed(seed);
});

test('the editor loads with the cookie and shows the seeded profile, its canvas, assets, telemetry and certificate', async ({
  editor,
}) => {
  const page = await editor();
  await expect(page.getByRole('heading', { name: 'Button Box Editor' })).toBeVisible();

  // Profiles: the seeded profile, active.
  const profiles = page.getByRole('region', { name: 'Profiles' });
  const starter = profiles.getByRole('listitem').filter({ hasText: 'ETS2 starter' });
  await expect(starter).toHaveCount(1);
  await expect(starter).toContainText('Active');
  await starter.getByRole('button', { name: /ETS2 starter/ }).click();

  // Canvas: the iPhone 14 Pro frame with the eight widgets of the seed.
  await expect(page.getByRole('tab', { name: 'iPhone 14 Pro', selected: true })).toBeVisible();
  const frame = page.locator('.pe-frame');
  await expect(frame).toBeVisible();
  await expect(frame.locator('[data-widget-id]')).toHaveCount(8);
  for (const id of ['engine', 'lights', 'wipers', 'high-beam', 'beacon', 'hazard', 'parking-brake', 'retarder']) {
    await expect(frame.locator(`[data-widget-id="${id}"]`)).toBeVisible();
  }

  const nav = page.getByRole('navigation', { name: 'Sections' });

  await nav.getByRole('button', { name: 'Assets' }).click();
  await expect(page.locator('.assets-row')).toHaveCount(3);
  await expect.poll(() => page.locator('.assets-thumb').evaluateAll((l) => l.every((i) => (i as HTMLImageElement).naturalWidth > 0))).toBe(true);

  await nav.getByRole('button', { name: 'Telemetry' }).click();
  await expect(page.locator('.telemetry-table tbody tr').first()).toBeVisible();

  await nav.getByRole('button', { name: 'Settings' }).click();
  const fingerprint = page.getByLabel('Certificate fingerprint');
  await expect(fingerprint).toBeVisible();
  expect((await fingerprint.innerText()).replace(/[^0-9A-F]/gi, '')).toMatch(/^[0-9A-F]{64}$/i);
});

test('a widget moved with the overlay and saved passes the host gate and moves on the phone', async ({ open, editor, editorApi }) => {
  const box = await open();
  const before = await box.widget('beacon').boundingBox();
  if (!before) throw new Error('Beacon is not visible on the phone.');

  const page = await editor();
  await page.getByRole('button', { name: /ETS2 starter/ }).click();
  await expect(page.locator('.pe-frame')).toBeVisible();

  // The seed fills its 8 x 3 grid, so a free cell comes from a fourth row.
  const rows = page.getByRole('spinbutton', { name: 'Rows' });
  await rows.fill('4');
  await rows.press('Enter');
  await expect(page.getByText('Unsaved changes', { exact: true })).toBeVisible();

  // Drag the beacon down by one cell with real pointer events on the overlay.
  const beacon = page.locator('.pe-box[aria-label="BEACON"]');
  await beacon.scrollIntoViewIfNeeded();
  const at = await beacon.boundingBox();
  if (!at) throw new Error('The beacon box is not visible in the editor.');
  const x = at.x + at.width / 2;
  const y = at.y + at.height / 2;
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.move(x, y + at.height / 2, { steps: 5 });
  await page.mouse.move(x, y + at.height, { steps: 5 });
  await page.mouse.up();

  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('Unsaved changes', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('alert')).toHaveCount(0); // no 422 problem list; the fixture also fails on any refused request

  // The host stored the move ...
  const saved = phoneWidget(await editorApi.profile(seed.id), 'beacon');
  expect(saved.cell).toMatchObject({ col: 0, row: 3 });
  // ... and the phone shows the beacon lower than before.
  await expect.poll(async () => (await box.widget('beacon').boundingBox())?.y ?? 0).toBeGreaterThan(before.y + 10);
});

test('keys pressed in another section never edit the hidden layout', async ({ editor }) => {
  // The Profiles section stays mounted while another section is shown (it keeps the unsaved draft), and its shortcuts listen
  // on the window. Arrow keys and Delete pressed on the Telemetry page must not move or remove the selected widget.
  const page = await editor();
  await page.getByRole('button', { name: /ETS2 starter/ }).click();
  const beacon = page.locator('.pe-box[aria-label="BEACON"]');
  await beacon.click();

  const nav = page.getByRole('navigation', { name: 'Sections' });
  await nav.getByRole('button', { name: 'Telemetry' }).click();
  await expect(page.locator('.telemetry-table')).toBeVisible();
  await page.locator('body').click({ position: { x: 5, y: 5 } });
  for (const key of ['ArrowDown', 'ArrowRight', 'Delete']) await page.keyboard.press(key);

  await nav.getByRole('button', { name: 'Profiles' }).click();
  await expect(page.locator('.pe-frame [data-widget-id]')).toHaveCount(8);
  await expect(page.getByText('Unsaved changes', { exact: true })).toHaveCount(0);
});
