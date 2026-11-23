import { expect, test } from './fixtures';
import { PHONE_TEMPLATE, type E2EProfile } from './host';
import { phoneWidget, readSeed, restoreSeed } from './seed';

// Gate 4 (decisions sections 3 and 11): a Save of the active profile and a switch of the active profile reach every connected
// phone in place; changes that are not saved never do. Real WebSocket layer, real stores, real editor bundle.
// The host is shared with the other specs: every test puts the seeded profile back (see seed.ts).
test.describe.configure({ mode: 'serial' });

let seed: E2EProfile;
test.beforeAll(async () => {
  seed = await readSeed();
});
test.afterEach(async () => {
  await restoreSeed(seed);
});

const names = (list: { name: string }[]) => list.map((r) => r.name);

test('Save of the active profile reaches a connected phone in place, without a reconnect', async ({ open, editorApi }) => {
  const box = await open();
  await expect(box.widget('beacon')).toBeVisible();
  await expect(box.page.getByText('BEACON', { exact: true })).toBeVisible();
  // From here on the page must stay the same page and the overlay must never show up.
  await box.page.evaluate(() => {
    window.__mark = 'same page';
    window.__reconnecting = false;
  });

  const profile = await editorApi.profile(seed.id);
  phoneWidget(profile, 'beacon').label = 'BEACON SAVED';
  await editorApi.save(profile);

  await expect(box.page.getByText('BEACON SAVED', { exact: true })).toBeVisible();
  await expect(box.page.getByText('BEACON', { exact: true })).toHaveCount(0);
  expect(await box.page.evaluate(() => ({ reconnecting: window.__reconnecting, mark: window.__mark }))).toEqual({
    reconnecting: false,
    mark: 'same page',
  });
  await expect(box.page.getByRole('status').filter({ hasText: 'Reconnecting' })).toHaveCount(0);

  // The grid is live: a tap on the republished widget still reaches the game and lights it.
  await box.tap('beacon');
  await expect.poll(async () => names(await box.received())).toEqual(['beacon']);
  await expect(box.widget('beacon')).toHaveAttribute('data-lit', 'true');
  await box.tap('beacon');
  await expect.poll(async () => names(await box.received())).toEqual(['beacon', 'beacon']);
});

test('changes in the editor stay off the phone until Save', async ({ open, editor, editorApi }) => {
  const box = await open();
  await expect(box.page.getByText('BEACON', { exact: true })).toBeVisible();

  const page = await editor();
  await page.getByRole('button', { name: /ETS2 starter/ }).click();
  await page.locator('.pe-box[aria-label="BEACON"]').click();
  const label = page.getByLabel('Label', { exact: true });
  await label.fill('BEACON EDITED');
  await label.press('Enter');

  // The editor knows the change is not saved ...
  await expect(page.getByText('Unsaved changes', { exact: true })).toBeVisible();
  // ... the canvas shows it ...
  await expect(page.locator('.pe-frame').getByText('BEACON EDITED', { exact: true })).toBeVisible();
  // ... and the phone keeps the old label. Absence cannot be polled for: give a stray publish time to arrive, then look.
  await page.waitForTimeout(700);
  await expect(box.page.getByText('BEACON', { exact: true })).toBeVisible();
  await expect(box.page.getByText('BEACON EDITED', { exact: true })).toHaveCount(0);
  expect(phoneWidget(await editorApi.profile(seed.id), 'beacon').label).toBe('BEACON');

  // Save in the editor itself: the host's gate accepts it (the fixture fails on any refused request) and the phone follows.
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(box.page.getByText('BEACON EDITED', { exact: true })).toBeVisible();
  await expect(page.getByText('Unsaved changes', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('alert')).toHaveCount(0);
  expect(phoneWidget(await editorApi.profile(seed.id), 'beacon').label).toBe('BEACON EDITED');
});

test('switching the active profile switches the phone; a profile without its layout gives "No layout"', async ({ open, editorApi }) => {
  const box = await open();
  await expect(box.widget('beacon')).toBeVisible();

  const copy = await editorApi.duplicate(seed.id);
  copy.layouts = copy.layouts.filter((l) => l.templateId !== PHONE_TEMPLATE);
  await editorApi.save(copy); // not active: nothing is published, the phone keeps its grid
  await expect(box.widget('beacon')).toBeVisible();

  await editorApi.activate(copy.id);
  await expect(box.page.getByText('No layout for this device in the active profile.')).toBeVisible();
  await expect(box.page.locator('.bbs-grid')).toHaveCount(0);

  await editorApi.activate(seed.id);
  await expect(box.page.locator('.bbs-grid')).toBeVisible();
  await expect(box.widget('beacon')).toBeVisible();
  await expect(box.page.getByText('No layout for this device in the active profile.')).toHaveCount(0);
});

test('a Save keeps a switch where it is; switching the profile away and back resets it to position 0', async ({ open, editorApi }) => {
  const box = await open();
  const wipers = box.widget('wipers');
  await expect(wipers).toHaveAttribute('data-position', '0');
  await box.tap('wipers', 0.75);
  await expect(wipers).toHaveAttribute('data-position', '1');

  // An unrelated change of the running profile: the knob must not jump back (decisions section 3 "Publishing").
  const profile = await editorApi.profile(seed.id);
  phoneWidget(profile, 'beacon').label = 'BEACON KEPT';
  await editorApi.save(profile);
  await expect(box.page.getByText('BEACON KEPT', { exact: true })).toBeVisible(); // the new layout is on the phone
  await expect(wipers).toHaveAttribute('data-position', '1');

  // A switch of the profile starts every switch at 0.
  const copy = await editorApi.duplicate(seed.id);
  await editorApi.activate(copy.id);
  await expect(wipers).toHaveAttribute('data-position', '0');
  await box.tap('wipers', 0.75);
  await expect(wipers).toHaveAttribute('data-position', '1');
  await editorApi.activate(seed.id);
  await expect(wipers).toHaveAttribute('data-position', '0');
});
