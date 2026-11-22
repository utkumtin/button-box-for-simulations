import { defineConfig, devices } from '@playwright/test';

// One WebKit project: iPhone 14 Pro landscape. Chromium and Firefox are not used (decisions section 13).
// The host is started by e2e/global-setup.ts; the client must be built first (`pnpm build`).
export default defineConfig({
  testDir: 'e2e',
  testMatch: '**/*.spec.ts',
  globalSetup: './e2e/global-setup.ts',
  // The host holds one game state shared by all tests, so they run one after another.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 30_000,
  expect: { timeout: 7_000 },
  reporter: [['list']],
  // Inside node_modules so no stray folder appears in the repo.
  outputDir: 'node_modules/.playwright-results',
  projects: [
    {
      name: 'webkit',
      use: {
        ...devices['Desktop Safari'],
        browserName: 'webkit',
        viewport: { width: 852, height: 393 },
        deviceScaleFactor: 3,
        hasTouch: true,
        isMobile: true,
        ignoreHTTPSErrors: true, // the host uses a throwaway CA
      },
    },
  ],
});
