import { expect, test } from '@playwright/test';

import { PLANS, answer, ask, conversationId, csrfToken, newPerson, openChat, uploadDocument } from './support';

test.describe('two people on the same system', () => {
  test('each sees and searches only their own documents, even under the same file name', async ({ browser }) => {
    const alice = await newPerson(browser, 'alice');
    const bob = await newPerson(browser, 'bob');

    await uploadDocument(alice.page, 'plans.md', PLANS);
    await expect(alice.page.getByText(/Added plans\.md/)).toBeVisible();

    await bob.page.goto('/documents');
    await expect(bob.page.getByText('No documents yet')).toBeVisible(); // nothing of Alice's

    // Bob uploads a file with Alice's file name and a different text: it is a new document of his, not a replacement of hers.
    await uploadDocument(bob.page, 'plans.md', '# Plans\n\n## Codename\n\nBob only plans a picnic, nothing secret.\n');
    await expect(bob.page.getByText(/Added plans\.md/)).toBeVisible();
    await expect(bob.page.getByText(/Replaced plans\.md/)).toHaveCount(0);

    // The same question goes to the real retrieval of two different tenants.
    await openChat(alice.page);
    await ask(alice.page, 'What is the launch codename?');
    await expect(answer(alice.page)).toContainText('Zephyrwood');

    await openChat(bob.page);
    await ask(bob.page, 'What is the launch codename?');
    await expect(answer(bob.page)).toContainText('picnic');
    await expect(bob.page.locator('body')).not.toContainText('Zephyrwood'); // her secret is nowhere on his page

    // Bob's replacing and deleting did not touch Alice's copy.
    await bob.page.goto('/documents');
    await bob.page.getByRole('button', { name: 'Delete plans.md' }).click();
    await bob.page.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(bob.page.getByText('Deleted plans.md.')).toBeVisible();

    await alice.page.goto('/documents');
    await expect(alice.page.getByRole('row', { name: /plans\.md/ })).toContainText('Indexed');
    await openChat(alice.page);
    await ask(alice.page, 'What is the launch codename again?');
    await expect(answer(alice.page).last()).toContainText('Zephyrwood');

    await alice.context.close();
    await bob.context.close();
  });

  test('one person cannot read, open or delete another\'s conversation', async ({ browser }) => {
    const alice = await newPerson(browser, 'alice');
    const bob = await newPerson(browser, 'bob');
    await uploadDocument(alice.page, 'plans.md', PLANS);
    await expect(alice.page.getByText(/Added plans\.md/)).toBeVisible();
    await openChat(alice.page);
    await ask(alice.page, 'What is the launch codename?');
    await expect(answer(alice.page)).toContainText('Zephyrwood');
    const alicesConversation = await conversationId(alice.page);

    // through the page: her address means nothing to him
    await bob.page.goto(`/chat/${alicesConversation}`);
    await expect(bob.page.getByRole('alert')).toContainText('That item no longer exists');
    await expect(bob.page.getByRole('link', { name: 'Start a new conversation' })).toBeVisible();
    await expect(bob.page.locator('body')).not.toContainText('Zephyrwood');

    // and straight at the API, with his own valid session and token
    const read = await bob.page.request.get(`/api/conversations/${alicesConversation}`);
    expect(read.status()).toBe(404);
    const remove = await bob.page.request.delete(`/api/conversations/${alicesConversation}`, { headers: { 'X-XSRF-TOKEN': await csrfToken(bob.context) } });
    expect(remove.status()).toBe(404);
    const write = await bob.page.request.post('/api/ask', {
      data: { question: 'Bob writes into her conversation', conversationId: alicesConversation },
      headers: { 'X-XSRF-TOKEN': await csrfToken(bob.context) },
    });
    expect(write.status()).toBe(404);

    // hers is untouched
    await alice.page.reload();
    await expect(answer(alice.page)).toContainText('Zephyrwood');
    await expect(alice.page.getByRole('article', { name: 'You asked' })).toHaveCount(1);

    await alice.context.close();
    await bob.context.close();
  });

  test('a request with a session but without the CSRF token is refused', async ({ browser }) => {
    const alice = await newPerson(browser, 'alice');

    const refused = await alice.page.request.post('/api/documents/sync'); // no X-XSRF-TOKEN

    expect(refused.status()).toBe(400);
    expect((await refused.json()).title).toContain('request token');
    await alice.context.close();
  });
});
