import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: ['admin-workflows.spec.ts', 'request-classifier.spec.ts'],
  fullyParallel: false,
  workers: 1,
  reporter: [['line']],
  use: {
    baseURL: 'http://127.0.0.1:4178',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    ...devices['Desktop Chrome']
  },
  webServer: {
    command: 'node test-server.mjs',
    url: 'http://127.0.0.1:4178/__e2e/health',
    reuseExistingServer: false,
    timeout: 30_000
  }
});
