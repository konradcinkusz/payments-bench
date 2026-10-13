import { test, expect } from '@playwright/test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const site = pathToFileURL(path.resolve(here, '..', '..', '..', 'docs', 'index.html')).href;

// The site must work from file:// with no network: that is the rule it is built under.
for (const scheme of ['light', 'dark'] as const) {
  test(`the documentation site renders from file:// in ${scheme} mode`, async ({ browser }) => {
    const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1280, height: 800 } });
    const page = await context.newPage();
    const failed: string[] = [];
    page.on('requestfailed', (r) => failed.push(r.url()));
    page.on('request', (r) => { if (/^https?:/.test(r.url())) failed.push(`external:${r.url()}`); });

    await page.goto(site);
    await expect(page.locator('h1').first()).toBeVisible();
    await expect(page.locator('svg[role="img"]').first()).toBeVisible();
    expect(failed, 'no external or failed requests').toEqual([]);

    await page.screenshot({ path: path.join(here, '..', 'screenshots', `site-${scheme}-hero.png`) });
    await page.screenshot({ path: path.join(here, '..', 'screenshots', `site-${scheme}-full.png`), fullPage: true });
    await context.close();
  });
}
