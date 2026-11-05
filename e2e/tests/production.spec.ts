import { expect, test } from '@playwright/test';

import { ask, answer, newAccount, openChat, PLANS, register, signOut, uploadDocument } from './support';

// What only the deployed shape can show: the built app served by the API, the headers a browser is given, the cookies it keeps.
// These run in the production stack (npm run test:production) and are skipped in the development one, which has no built app to check.
test.skip(process.env.E2E_STACK !== 'production', 'about the production build; run with npm run test:production');

const POLICY_PARTS = ["default-src 'self'", "script-src 'self'", "object-src 'none'", "frame-ancestors 'none'", "base-uri 'self'"];

test.describe('the production build', () => {
  test('the page comes with the hardening headers, and the policy allows no script but the app\'s own', async ({ page }) => {
    const response = await page.goto('/');

    const headers = response!.headers();
    for (const part of POLICY_PARTS) expect(headers['content-security-policy']).toContain(part);
    expect(headers['content-security-policy']).not.toContain("script-src 'self' 'unsafe-inline'");
    expect(headers['x-content-type-options']).toBe('nosniff');
    expect(headers['x-frame-options']).toBe('DENY');
    expect(headers['referrer-policy']).toBe('no-referrer');
    expect(headers['cache-control']).toBe('no-cache'); // the page names the current scripts, so the browser always asks
  });

  test('a whole session runs without the browser reporting a single policy violation', async ({ page }) => {
    const violations: string[] = [];
    await page.addInitScript(() => {
      (window as unknown as { __violations: string[] }).__violations = [];
      document.addEventListener('securitypolicyviolation', (e) => {
        (window as unknown as { __violations: string[] }).__violations.push(`${e.violatedDirective}: ${e.blockedURI}`);
      });
    });
    page.on('console', (message) => {
      if (/content security policy/i.test(message.text())) violations.push(message.text());
    });

    await register(page, newAccount('csp'));
    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page.getByText('plans.md')).toBeVisible();
    await openChat(page);
    await ask(page, 'What is the launch codename?');
    await expect(answer(page)).toBeVisible();
    await signOut(page);

    violations.push(...(await page.evaluate(() => (window as unknown as { __violations: string[] }).__violations)));
    expect(violations).toEqual([]);
  });

  test('reloading on a deep link brings the app back to that page, signed in', async ({ page }) => {
    await register(page, newAccount('reload'));
    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page).toHaveURL(/\/documents$/);

    await page.reload();

    await expect(page).toHaveURL(/\/documents$/);
    await expect(page.getByText('plans.md')).toBeVisible(); // the API served the app's page for an address that is only a route inside it
  });

  test('files named by their content are cached for good, and the ones that are not are always re-checked', async ({ page }) => {
    await page.goto('/');
    const files = await page.evaluate(() => [
      ...Array.from(document.querySelectorAll('script[src]'), (s) => (s as HTMLScriptElement).src),
      ...Array.from(document.querySelectorAll('link[rel=stylesheet]'), (l) => (l as HTMLLinkElement).href),
    ]);
    expect(files.length).toBeGreaterThanOrEqual(3);

    for (const file of files) {
      const response = await page.request.get(file);
      expect(response.status(), file).toBe(200);
      // theme-init.js keeps its own name (it has to: it is read before anything else), so it is the one file that must be re-checked every time
      const expected = file.endsWith('/theme-init.js') ? 'no-cache' : 'public, max-age=31536000, immutable';
      expect(response.headers()['cache-control'], file).toBe(expected);
    }
    expect(files.some((f) => f.endsWith('/theme-init.js')), 'the page loads the script that applies the remembered theme').toBe(true);
  });

  test('the typefaces are served from this origin, as fonts, and cached for good', async ({ page }) => {
    await page.goto('/login');
    await page.evaluate(() => document.fonts.ready);
    const stylesheet = await page.evaluate(() => (document.querySelector('link[rel=stylesheet]') as HTMLLinkElement).href);
    const css = await (await page.request.get(stylesheet)).text();
    const fonts = [...css.matchAll(/url\("?([^")]+\.woff2)"?\)/g)].map((m) => new URL(m[1], stylesheet).href);
    expect(fonts.length).toBeGreaterThanOrEqual(2);

    for (const font of fonts.slice(0, 3)) {
      const response = await page.request.get(font);
      expect(new URL(font).origin, 'no font comes from another origin').toBe(new URL(page.url()).origin);
      expect(response.status(), font).toBe(200);
      expect(response.headers()['content-type'], font).toContain('font/woff2');
      expect(response.headers()['cache-control'], font).toBe('public, max-age=31536000, immutable');
    }
    expect(await page.evaluate(() => document.fonts.check("16px 'Inter Variable'")), 'Inter is what the page is set in').toBe(true);
  });

  test('a missing file is a plain 404, not the app page', async ({ request }) => {
    const response = await request.get('/main-GONE0000.js');

    expect(response.status()).toBe(404);
    expect(response.headers()['content-type']).toContain('application/problem+json');
    expect(await response.text()).not.toContain('<app-root');
  });

  test('an unknown API address is a JSON 404 for a program to read, not a web page', async ({ request }) => {
    const response = await request.get('/api/nothing/here');

    expect(response.status()).toBe(404);
    expect(response.headers()['content-type']).toContain('application/problem+json');
    expect(response.headers()['cache-control']).toBe('no-store');
  });

  test('the API says nothing may keep a copy of what it returns', async ({ page }) => {
    await register(page, newAccount('nostore'));

    const response = await page.request.get('/api/documents');

    expect(response.status()).toBe(200);
    expect(response.headers()['cache-control']).toBe('no-store');
    expect(response.headers()['content-security-policy']).toBeTruthy(); // even a data response carries the policy
  });

  test('the health check answers a stranger and says nothing more than that it is up', async ({ request }) => {
    const response = await request.get('/healthz');

    expect(response.status()).toBe(200);
    expect(await response.json()).toEqual({ status: 'ok' });
  });

  test('behind the proxy the cookies are Secure, HttpOnly and SameSite=Strict, as they would be on https', async ({ page, context }) => {
    await register(page, newAccount('cookies'));

    const cookies = await context.cookies();
    for (const name of ['notebook.auth', 'notebook.antiforgery', 'XSRF-TOKEN']) {
      const cookie = cookies.find((c) => c.name === name);
      expect(cookie, name).toBeDefined();
      expect(cookie!.secure, `${name} is Secure`).toBe(true);
      expect(cookie!.sameSite, `${name} is SameSite=Strict`).toBe('Strict');
    }
    expect(cookies.find((c) => c.name === 'notebook.auth')!.httpOnly).toBe(true);
  });

  test('a visitor cannot name their own address or scheme: what the proxy says is what counts', async ({ request }) => {
    // The proxy overwrites these headers, so a claim of plain http (which would be refused) or of another address changes nothing.
    const response = await request.get('/healthz', { headers: { 'X-Forwarded-Proto': 'http', 'X-Forwarded-For': '203.0.113.7' } });

    expect(response.status()).toBe(200);
    const me = await request.get('/api/auth/me', { headers: { 'X-Forwarded-Proto': 'http' } });
    expect(me.status()).toBe(401); // a normal answer to a stranger, not the 500 for a plain-http request
  });
});
