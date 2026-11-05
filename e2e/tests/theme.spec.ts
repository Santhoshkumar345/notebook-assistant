import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

import { PLANS, answer, ask, conversationId, newAccount, openChat, register, uploadDocument } from './support';

// The colour choice is System (follow the device), Light or Dark. These run in a real browser because what matters is what is painted:
// that an explicit choice beats the device, that it is remembered, and that it is on the page before the app's own script has run.
const DARK_BACKGROUND = 'rgb(14, 16, 32)';
const LIGHT_BACKGROUND = 'rgb(243, 244, 251)';

const pickTheme = (page: Page, label: 'System' | 'Light' | 'Dark') => page.getByRole('radio', { name: label }).check({ force: true }); // the radio is drawn as an icon
const paintedBackground = (page: Page) => page.evaluate(() => getComputedStyle(document.body).backgroundColor);

test.describe('colour theme', () => {
  test('a dark choice beats a light device, and a light choice beats a dark one', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'light' });
    await page.goto('/login');
    expect(await paintedBackground(page)).toBe(LIGHT_BACKGROUND);

    await pickTheme(page, 'Dark');
    expect(await paintedBackground(page)).toBe(DARK_BACKGROUND);
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

    await page.emulateMedia({ colorScheme: 'dark' });
    await pickTheme(page, 'Light');
    expect(await paintedBackground(page)).toBe(LIGHT_BACKGROUND);
  });

  test('"System" follows the device, including when the device changes its mind', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto('/login');
    await expect(page.getByRole('radio', { name: 'System' })).toBeChecked();
    expect(await paintedBackground(page)).toBe(DARK_BACKGROUND);
    await expect(page.locator('html')).not.toHaveAttribute('data-theme', /.*/);

    await page.emulateMedia({ colorScheme: 'light' });
    expect(await paintedBackground(page)).toBe(LIGHT_BACKGROUND);
  });

  test('the choice is remembered across a reload and across pages, and going back to System forgets it', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'light' });
    await page.goto('/login');
    await pickTheme(page, 'Dark');

    await page.reload();
    await expect(page.getByRole('radio', { name: 'Dark' })).toBeChecked();
    expect(await paintedBackground(page)).toBe(DARK_BACKGROUND);

    await page.goto('/register');
    expect(await paintedBackground(page)).toBe(DARK_BACKGROUND);

    await pickTheme(page, 'System');
    await page.reload();
    await expect(page.getByRole('radio', { name: 'System' })).toBeChecked();
    expect(await paintedBackground(page)).toBe(LIGHT_BACKGROUND);
  });

  test('a remembered choice is on the page before the app has loaded, so there is no flash of the wrong theme', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'light' });
    await page.goto('/login');
    await pickTheme(page, 'Dark');

    // With the app's own script blocked, nothing but the small script in the page's head can have set the theme.
    await page.route(/\/main[^/]*\.js(\?.*)?$/, (route) => route.abort());
    await page.reload({ waitUntil: 'domcontentloaded' });

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    expect(await paintedBackground(page)).toBe(DARK_BACKGROUND);
  });

  test('a person who chose Dark stays in Dark after signing in and out', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'light' });
    await register(page, newAccount('themed'));
    await pickTheme(page, 'Dark');

    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/login/);

    expect(await paintedBackground(page)).toBe(DARK_BACKGROUND);
  });
});

test.describe('the colours are readable when the choice and the device disagree', () => {
  const RULES = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa', 'best-practice'];

  async function expectNoViolations(page: Page, where: string): Promise<void> {
    const { violations } = await new AxeBuilder({ page }).withTags(RULES).analyze();
    expect(violations.map((v) => `${v.id}: ${v.help} [${v.nodes.slice(0, 3).map((n) => n.target.join(' ')).join(' | ')}]`), where).toEqual([]);
  }

  for (const [device, choice] of [
    ['light', 'Dark'],
    ['dark', 'Light'],
  ] as const) {
    test(`a ${choice.toLowerCase()} choice on a ${device} device, on every screen`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: device });
      await register(page, newAccount(`contrary-${choice.toLowerCase()}`));
      await pickTheme(page, choice);
      const where = `${choice} chosen on a ${device} device`;

      await expect(page.getByText('No documents yet')).toBeVisible();
      await expectNoViolations(page, `the empty documents page, ${where}`);

      await uploadDocument(page, 'plans.md', PLANS);
      await expect(page.getByRole('row', { name: /plans\.md/ })).toContainText('Indexed');
      await expectNoViolations(page, `the documents page, ${where}`);

      await openChat(page);
      await ask(page, 'What is the launch codename?');
      await expect(answer(page)).toContainText('Zephyrwood');
      await conversationId(page);
      await expectNoViolations(page, `an answered chat, ${where}`);

      await page.getByRole('button', { name: 'Sign out' }).click();
      await expect(page).toHaveURL(/\/login/);
      await expectNoViolations(page, `the sign-in page, ${where}`);
    });
  }
});
