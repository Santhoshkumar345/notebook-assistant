import { randomBytes } from 'node:crypto';

import { expect, type Browser, type BrowserContext, type Page } from '@playwright/test';

export interface Account {
  email: string;
  password: string;
}

/** A new person. The password is random and lives only in memory for this test: it is never printed, and tracing is off. */
export function newAccount(label: string): Account {
  return {
    email: `${label}-${randomBytes(4).toString('hex')}@example.test`,
    password: `${randomBytes(12).toString('hex')} ${randomBytes(4).toString('hex')}`,
  };
}

export async function register(page: Page, account: Account): Promise<void> {
  await page.goto('/register');
  await page.getByLabel('Email').fill(account.email);
  await page.getByLabel('Password', { exact: true }).fill(account.password);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page).toHaveURL(/\/documents$/); // a new account starts at the documents page
  await expect(page.locator('header .who')).toHaveText(account.email);
}

/** Fills in and submits the sign-in form. Goes to `to` first if given; otherwise uses the page as it is (which must be the sign-in page). */
export async function signIn(page: Page, account: Account, to?: string): Promise<void> {
  if (to) await page.goto(to);
  await page.getByLabel('Email').fill(account.email);
  await page.getByLabel('Password', { exact: true }).fill(account.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

export async function signOut(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page).toHaveURL(/\/login/);
}

/** Someone with a browser of their own: separate cookies, storage and session. */
export async function newPerson(browser: Browser, label: string): Promise<{ account: Account; context: BrowserContext; page: Page }> {
  const context = await browser.newContext({ baseURL: process.env.E2E_BASE_URL ?? 'http://127.0.0.1:4210' });
  const page = await context.newPage();
  const account = newAccount(label);
  await register(page, account);
  return { account, context, page };
}

export async function uploadDocument(page: Page, name: string, content: string): Promise<void> {
  await page.goto('/documents');
  await page.locator('input[type=file]').setInputFiles({ name, mimeType: 'text/markdown', buffer: Buffer.from(content) });
}

export async function openChat(page: Page): Promise<void> {
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Chat' }).click();
  await expect(page).toHaveURL(/\/chat/);
}

/** Types a question (or an answer to a clarifying question) and sends it with the button. */
export async function ask(page: Page, text: string, options: { agent?: boolean } = {}): Promise<void> {
  if (options.agent) await page.getByLabel('Agent').check();
  await page.getByLabel(/^Your (question|answer)$/).fill(text);
  await page.getByRole('button', { name: /^(Ask|Send answer)$/ }).click();
}

// exact: role names match by substring, and the pending question is labelled "Your question, waiting for an answer"
export const answer = (page: Page) => page.getByRole('article', { name: 'Answer', exact: true });

/** The id in the address of the conversation on screen. The answer appears a moment before the address changes, so this waits for it. */
export async function conversationId(page: Page): Promise<string> {
  await expect(page).toHaveURL(/\/chat\/[0-9a-f-]{36}$/);
  return /\/chat\/([0-9a-f-]{36})/.exec(page.url())![1];
}

/** The CSRF token the page holds, for tests that make requests the page would not. */
export async function csrfToken(context: BrowserContext): Promise<string> {
  const cookie = (await context.cookies()).find((c) => c.name === 'XSRF-TOKEN');
  if (!cookie) throw new Error('no XSRF-TOKEN cookie');
  return decodeURIComponent(cookie.value);
}

export const PLANS = '# Plans\n\n## Codename\n\nThe launch codename is Zephyrwood and only Alice may learn it.\n\n## Budget\n\nThe launch budget is forty thousand.\n';
