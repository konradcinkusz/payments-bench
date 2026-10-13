import { defineConfig } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');

// The API and the partner simulator are started by Playwright unless they are already
// running (reuseExistingServer), which is how a developer keeps them up between runs.
// Both run in the degraded in-memory mode: no database, no authority, nothing durable.
export const apiUrl = process.env.PB_API_URL ?? 'http://localhost:5100';
export const partnerUrl = process.env.PB_PARTNER_URL ?? 'http://localhost:5101';

export default defineConfig({
  testDir: './tests',
  outputDir: './test-results',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  reporter: [['list'], ['html', { open: 'never', outputFolder: './playwright-report' }]],
  use: {
    baseURL: apiUrl,
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
  },
  webServer: [
    {
      command: `dotnet run --project ${path.join(repoRoot, 'src/PaymentsBench.PartnerSimulator')} -c Release --no-build --urls ${partnerUrl}`,
      url: `${partnerUrl}/alive`,
      reuseExistingServer: true,
      timeout: 120_000,
    },
    {
      command: `dotnet run --project ${path.join(repoRoot, 'src/PaymentsBench.Api')} -c Release --no-build --urls ${apiUrl}`,
      url: `${apiUrl}/alive`,
      reuseExistingServer: true,
      timeout: 120_000,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', Partner__BaseAddress: partnerUrl },
    },
  ],
});
