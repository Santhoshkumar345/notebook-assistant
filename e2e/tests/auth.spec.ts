import { expect, test } from '@playwright/test';

import { csrfToken, newAccount, newPerson, register, signIn, signOut } from './support';

test.describe('signing in and out', () => {
  test('a signed-out visitor is sent to sign in, and after signing in lands where they were going', async ({ page }) => {
    const account = newAccount('visitor');
    await register(page, account);
    await signOut(page);

    await page.goto('/documents');
    await expect(page).toHaveURL('/login?returnUrl=%2Fdocuments');
    await expect(page.locator('header nav')).toHaveCount(0); // nothing person-specific while signed out
    await expect(page.getByRole('button', { name: 'Sign out' })).toHaveCount(0);

    await page.getByLabel('Email').fill(account.email);
    await page.getByLabel('Password', { exact: true }).fill(account.password);
    await page.getByRole('button', { name: 'Sign in' }).click();

    await expect(page).toHaveURL('/documents');
    await expect(page.locator('header .who')).toHaveText(account.email);
  });

  test('the session cookie is hidden from the page, and ends when the person signs out', async ({ page, context }) => {
    await register(page, newAccount('cookie'));

    const visibleToScript = await page.evaluate(() => document.cookie);
    expect(visibleToScript).not.toContain('notebook.auth'); // HttpOnly: injected script cannot read the session
    expect((await context.cookies()).find((c) => c.name === 'notebook.auth')).toMatchObject({ httpOnly: true, sameSite: 'Strict' });

    await signOut(page);

    const afterwards = await page.request.get('/api/documents');
    expect(afterwards.status()).toBe(401); // really over on the server, not just hidden
  });

  test('a person who is already signed in is taken past the sign-in page', async ({ page }) => {
    await register(page, newAccount('already'));

    await page.goto('/login');

    await expect(page).toHaveURL('/chat');
  });

  test('a returnUrl that leaves the app is ignored', async ({ page, baseURL }) => {
    const account = newAccount('redirect');
    await register(page, account);
    await signOut(page);

    await signIn(page, account, '/login?returnUrl=https%3A%2F%2Fevil.example%2Fphish');

    await expect(page).toHaveURL('/chat');
    expect(new URL(page.url()).origin).toBe(baseURL);
  });

  test('wrong details get the same words whether or not the email exists, and a failed attempt empties the password box', async ({ page }) => {
    const account = newAccount('wrong');
    await register(page, account);
    await signOut(page);

    await signIn(page, { email: account.email, password: 'definitely not the password' });
    await expect(page.getByRole('alert')).toHaveText('Email or password is incorrect.');
    await expect(page.getByLabel('Password', { exact: true })).toHaveValue('');

    await signIn(page, newAccount('nobody'));
    await expect(page.getByRole('alert')).toHaveText('Email or password is incorrect.');
  });

  test('five wrong passwords lock the account, and then even the right one is refused', async ({ page }) => {
    const account = newAccount('lock');
    await register(page, account);
    await signOut(page);

    await page.goto('/login');
    for (let attempt = 1; attempt <= 4; attempt++) {
      await page.getByLabel('Email').fill(account.email);
      await page.getByLabel('Password', { exact: true }).fill(`wrong password number ${attempt}`);
      await page.getByRole('button', { name: 'Sign in' }).click();
      await expect(page.getByRole('alert')).toHaveText('Email or password is incorrect.');
    }

    await page.getByLabel('Email').fill(account.email);
    await page.getByLabel('Password', { exact: true }).fill('wrong password number 5');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toContainText(/Too many attempts\. Try again in about \d+ seconds\./);

    await page.getByLabel('Email').fill(account.email);
    await page.getByLabel('Password', { exact: true }).fill(account.password); // the correct one
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toContainText('Too many attempts');
    await expect(page).toHaveURL(/\/login/);
  });

  test('a password under twelve characters is explained as it is typed and cannot be submitted', async ({ page }) => {
    await page.goto('/register');
    await page.getByLabel('Email').fill(newAccount('short').email);
    await page.getByLabel('Password', { exact: true }).fill('too short');

    await expect(page.getByText('Use at least 12 characters.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Create account' })).toBeDisabled();

    await page.getByLabel('Password', { exact: true }).fill('a few ordinary words');
    await expect(page.getByRole('button', { name: 'Create account' })).toBeEnabled();
  });

  test('registering an email that is taken is refused in the server\'s words', async ({ page, browser }) => {
    const first = await newPerson(browser, 'taken');

    await page.goto('/register');
    await page.getByLabel('Email').fill(first.account.email);
    await page.getByLabel('Password', { exact: true }).fill(newAccount('x').password);
    await page.getByRole('button', { name: 'Create account' }).click();

    await expect(page.getByRole('alert')).toHaveText('That email address is already registered.');
    await first.context.close();
  });

  test('a session that ends behind the page\'s back shows a notice, and clears the page', async ({ page, context }) => {
    await register(page, newAccount('expired'));
    await page.evaluate(() => ((window as unknown as { marker: string }).marker = 'memory of the old session'));

    // the session ends "somewhere else" (another tab, another device)
    const ended = await page.request.post('/api/auth/logout', { headers: { 'X-XSRF-TOKEN': await csrfToken(context) } });
    expect(ended.status()).toBe(204);

    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Chat' }).click(); // the page still believes it is signed in

    await expect(page).toHaveURL('/login?expired=1');
    await expect(page.getByText('Your session ended. Sign in again to continue.')).toBeVisible();
    expect(await page.evaluate(() => (window as unknown as { marker?: string }).marker)).toBeUndefined(); // a full page load, so nothing lingered
    await expect(page.locator('header .who')).toHaveCount(0);
  });

  test('the sign-in form still works, with no reload, after another session signed in and out on the same browser', async ({ page, context }) => {
    // A second tab shares this one's cookies. While this page sits on the sign-in form, that tab registers someone and signs them out again,
    // which changes who the browser is and so which CSRF token fits. Each response hands over a token for the new identity; without that,
    // this form was refused ("request token invalid") until the page was reloaded.
    await page.goto('/login');
    const other = newAccount('elsewhere');
    const registered = await page.request.post('/api/auth/register', { data: other, headers: { 'X-XSRF-TOKEN': await csrfToken(context) } });
    expect(registered.status()).toBe(201);
    const loggedOut = await page.request.post('/api/auth/logout', { headers: { 'X-XSRF-TOKEN': await csrfToken(context) } });
    expect(loggedOut.status()).toBe(204);

    await signIn(page, other); // no goto: the form this page already has

    await expect(page).toHaveURL('/chat');
  });
});
