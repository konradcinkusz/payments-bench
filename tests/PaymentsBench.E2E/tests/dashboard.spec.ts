import { test, expect } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const dashboard = process.env.PB_DASHBOARD_URL;

// Opt-in: only when a standalone OpenTelemetry dashboard is running and the API exports to it.
// It runs after api-flows.spec.ts (alphabetical order, one worker), so the traces exist.
test.skip(!dashboard, 'PB_DASHBOARD_URL is not set');

test('the traces of the walk-through are visible on the dashboard', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`${dashboard}/traces`);
  await expect(page.getByText(/Showing \d+ traces/)).toBeVisible({ timeout: 30_000 });

  // Narrow the list to the submit calls: one of them is the timeout-after-accept, seconds long,
  // next to the millisecond ones, which is the point of the picture.
  await page.getByRole('searchbox').first().fill('submit');
  await page.waitForTimeout(1500);
  await page.screenshot({ path: path.join(here, '..', 'screenshots', 'dashboard-traces.png') });

  // The timeout-after-accept submit is the one measured in seconds; open it if it is there.
  const rows = page.getByRole('row').filter({ hasText: /submit/ });
  const slow = rows.filter({ hasText: /\b[1-9]\d*(\.\d+)?s\b/ }).first();
  const row = (await slow.count()) > 0 ? slow : rows.first();
  await row.getByText(/submit/).first().click();
  await expect(page.getByText(/Trace detail|Span/i).first()).toBeVisible({ timeout: 30_000 });
  await page.waitForTimeout(1500);
  await page.screenshot({ path: path.join(here, '..', 'screenshots', 'dashboard-trace-detail.png') });
});
