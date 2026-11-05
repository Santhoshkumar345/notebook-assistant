import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

import { expect, test } from '@playwright/test';

import { answer, ask, newAccount, openChat, register, uploadDocument } from '../tests/support';

/**
 * Regenerates the pictures in docs/screenshots with the REAL model (npm run screenshots; it needs GEMINI_API_KEY in ai-service/.env and makes
 * about five model calls). Everything else is the same stack the end-to-end tests use, on a throwaway database.
 */
const out = resolve(__dirname, '..', '..', 'docs', 'screenshots');

const NOTES = `# Garden Project Notes

## Budget

The total budget for the garden project is 4,500 euros. The plants take 1,200 euros and the greenhouse takes 2,100 euros.
The remaining 1,200 euros covers tools and soil.

## Schedule

Digging starts on 4 April. The greenhouse is delivered in the second week of May, and planting finishes by the end of May.

## Contacts

The landscaper is Maria Costa. The greenhouse supplier is Northfield Glass, and they deliver on weekdays only.
`;

test('regenerate the screenshots', async ({ page }) => {
  test.setTimeout(240_000);
  mkdirSync(out, { recursive: true });
  await page.setViewportSize({ width: 1200, height: 780 });
  await page.emulateMedia({ colorScheme: 'light' });

  await page.goto('/login');
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await page.screenshot({ path: `${out}/sign-in.png` });

  await register(page, newAccount('garden'));
  await uploadDocument(page, 'garden-notes.md', NOTES);
  await expect(page.getByRole('row', { name: /garden-notes\.md/ })).toContainText('Indexed', { timeout: 40_000 }); // the first upload after start-up loads the embedding model
  await page.screenshot({ path: `${out}/documents.png` });

  await openChat(page);
  await ask(page, 'How much does the greenhouse cost, and when is it delivered?');
  await expect(answer(page)).toBeVisible({ timeout: 90_000 });
  await answer(page).locator('summary').first().click();
  await page.screenshot({ path: `${out}/chat-answer.png` });

  await page.getByRole('link', { name: 'New conversation' }).click();
  await ask(page, 'What was the limit?', { agent: true });
  await expect(page.getByRole('article', { name: 'Needs one more detail' })).toBeVisible({ timeout: 90_000 });
  await page.screenshot({ path: `${out}/chat-clarify.png` });

  await page.getByRole('radio', { name: 'Dark' }).check({ force: true }); // the toggle in the top bar, not the device setting
  await page.screenshot({ path: `${out}/chat-dark.png` });
  await page.getByRole('radio', { name: 'System' }).check({ force: true });

  await page.setViewportSize({ width: 390, height: 780 });
  await page.screenshot({ path: `${out}/chat-phone.png` });
  await page.goto('/documents');
  await expect(page.getByRole('row', { name: /garden-notes\.md/ })).toContainText('Indexed', { timeout: 40_000 });
  await page.screenshot({ path: `${out}/documents-phone.png` });
});
