import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  workers: 1, // The browser test host controls one shared provider and can restart its Kestrel API.
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: 'http://localhost:4201',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'desktop-chromium', use: { ...devices['Desktop Chrome'] } },
    {
      name: 'mobile-chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 375, height: 812 },
        isMobile: true,
        hasTouch: true,
      },
    },
  ],
  webServer: [
    {
      command: 'dotnet run --project ../backend/ColoringApp.BrowserTests.Host --no-build',
      url: 'http://localhost:5214/api/health/ready',
      timeout: 240_000,
      reuseExistingServer: false,
      gracefulShutdown: { signal: 'SIGTERM', timeout: 15_000 },
    },
    {
      command: 'npm start -- --port 4201 --proxy-config proxy.e2e.conf.json',
      url: 'http://localhost:4201',
      timeout: 90_000,
      reuseExistingServer: false,
      gracefulShutdown: { signal: 'SIGTERM', timeout: 10_000 },
    },
  ],
});
