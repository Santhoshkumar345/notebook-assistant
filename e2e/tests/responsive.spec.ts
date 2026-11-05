import { expect, test, type Page } from '@playwright/test';

import { answer, ask, conversationId, newAccount, openChat, PLANS, register, signOut, uploadDocument } from './support';

// Phones are 320 to 430 CSS pixels wide, tablets 768 to 1024, and laptops from about 1280. The page has to work at all of them, not only at the two sizes it was drawn at.
const WIDTHS = [320, 375, 768, 1024, 1440];

/** How far the page can be scrolled sideways. Anything above zero means something is wider than the screen. */
const sidewaysOverflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

async function atEveryWidth(page: Page, what: string, check: (width: number) => Promise<void>): Promise<void> {
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 800 });
    await check(width);
    expect(await sidewaysOverflow(page), `${what} scrolls sideways at ${width}px`).toBeLessThanOrEqual(0);
  }
}

test.describe('every page at every common width', () => {
  test('the sign-in and registration pages never scroll sideways', async ({ page }) => {
    for (const path of ['/login', '/register']) {
      await atEveryWidth(page, path, async () => {
        await page.goto(path);
        await expect(page.getByRole('button', { name: /Sign in|Create account/ })).toBeVisible();
      });
    }
  });

  test('the documents and chat pages never scroll sideways, and everything a person needs stays on screen', async ({ page }) => {
    await register(page, newAccount('responsive'));
    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page.getByText(/Added plans\.md/)).toBeVisible(); // indexed, so it can be asked about
    await openChat(page);
    await ask(page, 'What is the launch codename?');
    await expect(answer(page)).toBeVisible();
    await conversationId(page);
    const chatUrl = page.url();

    await atEveryWidth(page, 'the documents page', async (width) => {
      await page.goto('/documents');
      await expect(page.getByRole('button', { name: 'Re-index plans.md' })).toBeVisible();
      for (const name of ['Sign out', 'Upload .md file']) {
        const box = await page.getByRole(name === 'Upload .md file' ? 'link' : 'button', { name }).or(page.getByText(name, { exact: true })).first().boundingBox();
        expect(box, `${name} at ${width}px`).not.toBeNull();
        expect(box!.x + box!.width, `${name} is pushed off the right edge at ${width}px`).toBeLessThanOrEqual(width + 1);
      }
    });

    await atEveryWidth(page, 'the chat page', async (width) => {
      await page.goto(chatUrl);
      await expect(answer(page)).toBeVisible();
      const box = await page.getByLabel(/^Your (question|answer)$/).boundingBox();
      expect(box!.x + box!.width, `the question box overflows at ${width}px`).toBeLessThanOrEqual(width + 1);
      expect(box!.x, `the question box starts off the left edge at ${width}px`).toBeGreaterThanOrEqual(0);
    });
  });

  test('on a phone the top bar leaves room for the page, and the theme and sign-out controls are within reach', async ({ page }) => {
    await register(page, newAccount('phonebar'));

    for (const width of [320, 375, 430]) {
      await page.setViewportSize({ width, height: 800 });
      await page.goto('/documents');
      const bar = await page.locator('header.topbar').boundingBox();
      expect(bar!.height, `the top bar is ${bar!.height}px tall at ${width}px`).toBeLessThanOrEqual(width === 320 ? 140 : 100);

      for (const control of [page.getByRole('button', { name: 'Sign out' }), page.getByRole('group', { name: 'Colour theme' })]) {
        const box = await control.boundingBox();
        expect(box, `a control is missing at ${width}px`).not.toBeNull();
        expect(box!.x + box!.width).toBeLessThanOrEqual(width + 1);
      }
    }
    await signOut(page);
  });
});
