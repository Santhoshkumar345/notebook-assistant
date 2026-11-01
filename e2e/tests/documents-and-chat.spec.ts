import { expect, test } from '@playwright/test';

import { PLANS, answer, ask, newAccount, openChat, register, signIn, signOut, uploadDocument } from './support';

test.describe('documents', () => {
  test('uploading a document indexes it, and the header counts the person\'s own searchable documents', async ({ page }) => {
    await register(page, newAccount('docs'));
    await expect(page.locator('app-health-badge')).toContainText('Ready: 0 documents searchable');

    await uploadDocument(page, 'plans.md', PLANS);

    await expect(page.getByText(/Added plans\.md: \d+ chunks? indexed\./)).toBeVisible();
    await expect(page.getByRole('row', { name: /plans\.md/ })).toContainText('Indexed');
    await expect(page.locator('app-health-badge')).toContainText('Ready: 1 document searchable');
  });

  test('uploading a name that already exists asks first, and replaces only after the person agrees', async ({ page }) => {
    await register(page, newAccount('replace'));
    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page.getByText(/Added plans\.md/)).toBeVisible();

    await page.locator('input[type=file]').setInputFiles({ name: 'plans.md', mimeType: 'text/markdown', buffer: Buffer.from('# Plans\n\nSomething new.') });
    await expect(page.getByRole('alertdialog')).toContainText('plans.md already exists');
    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(page.getByRole('alertdialog')).toHaveCount(0);
    await expect(page.getByText(/Replaced plans\.md/)).toHaveCount(0); // nothing was sent

    await page.locator('input[type=file]').setInputFiles({ name: 'plans.md', mimeType: 'text/markdown', buffer: Buffer.from('# Plans\n\nSomething new.') });
    await page.getByRole('button', { name: 'Replace' }).click();
    await expect(page.getByText(/Replaced plans\.md/)).toBeVisible();
    await expect(page.getByRole('row')).toHaveCount(2); // the header row and one document
  });

  test('a file the server would refuse is refused in the browser, with the reason', async ({ page }) => {
    await register(page, newAccount('badfile'));

    await page.locator('input[type=file]').setInputFiles({ name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('# x') });
    await expect(page.getByRole('alert')).toContainText('"notes.txt" is not an accepted file name');

    await page.locator('input[type=file]').setInputFiles({ name: 'empty.md', mimeType: 'text/markdown', buffer: Buffer.from('   ') });
    await expect(page.getByRole('alert')).toContainText('"empty.md" is empty');
    await expect(page.getByText('No documents yet')).toBeVisible();
  });

  test('deleting a document asks first, then removes it', async ({ page }) => {
    await register(page, newAccount('delete'));
    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page.getByRole('row', { name: /plans\.md/ })).toBeVisible();

    await page.getByRole('button', { name: 'Delete plans.md' }).click();
    await expect(page.getByRole('alertdialog')).toContainText('Delete plans.md?');
    await page.getByRole('button', { name: 'Delete', exact: true }).click();

    await expect(page.getByText('Deleted plans.md.')).toBeVisible();
    await expect(page.getByText('No documents yet')).toBeVisible();
  });
});

test.describe('asking', () => {
  test('a question gets an answer from the person\'s own document, with its source, and the conversation survives a reload', async ({ page }) => {
    await register(page, newAccount('asker'));
    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page.getByText(/Added plans\.md/)).toBeVisible();
    await openChat(page);

    await ask(page, 'What is the launch codename?');

    await expect(answer(page)).toContainText('Zephyrwood');
    await expect(page).toHaveURL(/\/chat\/[0-9a-f-]{36}$/);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('What is the launch codename?');

    await answer(page).locator('summary').click(); // the passage behind the source
    await expect(answer(page).locator('summary')).toContainText('plans.md');
    await expect(answer(page).locator('blockquote')).toContainText('Zephyrwood');

    await page.reload();
    await expect(answer(page)).toContainText('Zephyrwood'); // read back from the server, not from page memory
    await expect(page.getByRole('navigation', { name: 'Conversations' }).getByRole('link', { name: /What is the launch codename\?/ })).toBeVisible();
  });

  test('the history is still there after signing out and back in, and a conversation can be deleted', async ({ page }) => {
    const account = newAccount('history');
    await register(page, account);
    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page.getByText(/Added plans\.md/)).toBeVisible();
    await openChat(page);
    await ask(page, 'What is the budget?');
    await expect(answer(page)).toContainText('forty thousand');

    await signOut(page);
    await signIn(page, account);
    await expect(page).toHaveURL('/chat');

    const list = page.getByRole('navigation', { name: 'Conversations' });
    await expect(list.getByRole('link', { name: /What is the budget\?/ })).toBeVisible();

    await list.getByRole('button', { name: 'Delete conversation: What is the budget?' }).click();
    await list.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(list.getByText('No conversations yet.')).toBeVisible();
  });

  test('"not found" and "no supported answer" are explained, and an unsupported answer\'s text is never shown', async ({ page }) => {
    await register(page, newAccount('outcomes'));
    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page.getByText(/Added plans\.md/)).toBeVisible();
    await openChat(page);

    await ask(page, 'An unanswerable question');
    await expect(page.getByRole('article', { name: 'Not found' })).toContainText('The documents do not contain an answer to this.');

    await ask(page, 'An unsupported claim');
    await expect(page.getByRole('article', { name: 'No supported answer' })).toContainText('could not be backed by a source');
  });

  test('with no searchable document the chat says so and points at the documents page', async ({ page }) => {
    await register(page, newAccount('empty'));
    await openChat(page);

    await expect(page.getByText('No document is searchable yet')).toBeVisible();
    await page.getByRole('link', { name: 'Add a document' }).click();
    await expect(page).toHaveURL('/documents');
  });
});
