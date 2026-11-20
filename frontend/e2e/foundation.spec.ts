import { expect, test } from '@playwright/test';

test('reaches readiness through Angular, the API, and migrated PostgreSQL', async ({
  page,
}, testInfo) => {
  const readiness = page.waitForResponse('/api/health/ready');
  await page.goto('/');
  const response = await readiness;
  expect(response.status()).toBe(200);
  expect(await response.json()).toEqual({
    status: 'ok',
    application: 'ColoringApp',
    database: 'ok',
  });
  await expect(page.getByRole('heading', { level: 1, name: 'ColoringApp' })).toBeVisible();
  await expect(page.getByRole('status')).toHaveText('Aplikacja jest gotowa.');
  await expect(page.getByRole('button', { name: 'Nagraj pomysł' })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath('foundation-ready.png'), fullPage: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
});

test('announces failure and supports a keyboard retry without duplicate requests', async ({
  page,
}, testInfo) => {
  let requests = 0;
  let completeRetry: (() => void) | undefined;
  const retryPending = new Promise<void>((resolve) => {
    completeRetry = resolve;
  });
  await page.route('**/api/health/ready', async (route) => {
    requests++;
    if (requests === 1) {
      await route.fulfill({ status: 503, json: { code: 'database_unavailable' } });
    } else {
      await retryPending;
      await route.continue();
    }
  });
  await page.goto('/');
  await expect(page.getByRole('status')).toHaveText(
    'Nie udało się połączyć z aplikacją. Spróbuj ponownie.',
  );
  const retry = page.getByRole('button', { name: 'Spróbuj ponownie' });
  await page.keyboard.press('Tab');
  await expect(retry).toBeFocused();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.screenshot({
    path: testInfo.outputPath('foundation-failure-focus.png'),
    fullPage: true,
  });
  expect(await retry.evaluate((button) => getComputedStyle(button).outlineStyle)).not.toBe('none');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('status')).toHaveText('Sprawdzamy gotowość aplikacji…');
  await expect(retry).toBeDisabled();
  await page.keyboard.press('Enter');
  expect(requests).toBe(2);
  completeRetry?.();
  await expect(page.getByRole('status')).toHaveText('Aplikacja jest gotowa.');
  expect(requests).toBe(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
});
