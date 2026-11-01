import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

import { PLANS, answer, ask, newAccount, openChat, register, uploadDocument } from './support';

/**
 * An automated scan (axe-core) of every screen, in both colour schemes and at phone width. It finds what a machine can find: missing names,
 * contrast, landmarks, target sizes, label mistakes. It cannot judge whether a screen makes sense to someone using a screen reader, so it
 * complements a manual pass and does not replace one.
 */
const RULES = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa', 'best-practice'];

async function expectNoViolations(page: Page, where: string): Promise<void> {
  const { violations } = await new AxeBuilder({ page }).withTags(RULES).analyze();
  const summary = violations.map((v) => `${v.id}: ${v.help} [${v.nodes.slice(0, 3).map((n) => n.target.join(' ')).join(' | ')}]`);
  expect(summary, `accessibility violations on ${where}`).toEqual([]);
}

const schemes = ['light', 'dark'] as const;

async function scan(page: Page, where: string): Promise<void> {
  for (const colorScheme of schemes) {
    await page.emulateMedia({ colorScheme });
    await expectNoViolations(page, `${where} (${colorScheme})`);
  }
  await page.emulateMedia({ colorScheme: 'light' });
  await page.setViewportSize({ width: 375, height: 812 });
  await expectNoViolations(page, `${where} (phone width)`);
  await page.setViewportSize({ width: 1280, height: 800 });
}

test.describe('accessibility scan', () => {
  test('the sign-in and registration pages', async ({ page }) => {
    await page.goto('/login');
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
    await scan(page, 'the sign-in page');

    await page.getByLabel('Email').fill('nobody@example.test');
    await page.getByLabel('Password', { exact: true }).fill('not the password');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await scan(page, 'the sign-in page showing an error');

    await page.goto('/register');
    await expect(page.getByRole('heading', { name: 'Create an account' })).toBeVisible();
    await page.getByLabel('Password', { exact: true }).fill('too short');
    await scan(page, 'the registration page with a short password');
  });

  test('the documents page, empty and with documents in different states', async ({ page }) => {
    await register(page, newAccount('a11y-docs'));
    await expect(page.getByText('No documents yet')).toBeVisible();
    await scan(page, 'the empty documents page');

    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page.getByRole('row', { name: /plans\.md/ })).toContainText('Indexed');
    await scan(page, 'the documents page with a document');

    await page.getByRole('button', { name: 'Delete plans.md' }).click();
    await expect(page.getByRole('alertdialog')).toBeVisible();
    await scan(page, 'the documents page asking to confirm a delete');
  });

  test('the chat page: empty, answered with sources, not found, unsupported, and a clarifying question', async ({ page }) => {
    await register(page, newAccount('a11y-chat'));
    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page.getByText(/Added plans\.md/)).toBeVisible();
    await openChat(page);
    await expect(page.getByText('Ask your documents')).toBeVisible();
    await scan(page, 'the empty chat page');

    await ask(page, 'What is the launch codename?');
    await expect(answer(page)).toContainText('Zephyrwood');
    await answer(page).locator('summary').click();
    await scan(page, 'the chat page with an answer and an open source');

    await ask(page, 'An unanswerable question');
    await expect(page.getByRole('article', { name: 'Not found' })).toBeVisible();
    await scan(page, 'the chat page with a not-found answer');

    await ask(page, 'An unsupported claim');
    await expect(page.getByRole('article', { name: 'No supported answer' })).toBeVisible();
    await scan(page, 'the chat page with an unsupported answer');

    await page.getByRole('link', { name: 'New conversation' }).click();
    await ask(page, 'What about that vague?', { agent: true });
    await expect(page.getByRole('article', { name: 'Needs one more detail' })).toBeVisible();
    await scan(page, 'the chat page waiting for an answer to a clarifying question');
  });
});
