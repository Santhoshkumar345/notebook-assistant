import { expect, test } from '@playwright/test';

import { PLANS, ask, conversationId, newAccount, openChat, register, uploadDocument } from './support';

// The stand-in model rewrites a follow-up that starts with "and " into the last thing the person asked followed by the follow-up (ai-service/e2e_server.py),
// and echoes the question it was asked ("[asked: ...]"). That makes the whole chain visible from the page: what the API saved and sent, what the AI service
// received, and what the page says was searched for.
const answers = (page: import('@playwright/test').Page) => page.getByRole('article', { name: 'Answer', exact: true });

test.describe('follow-up questions', () => {
  test('a follow-up is understood from the conversation, and the page says what was searched for, also after a reload', async ({ page }) => {
    await register(page, newAccount('followup'));
    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page.getByText(/Added plans\.md/)).toBeVisible();
    await openChat(page);

    await ask(page, 'What is the launch codename?');
    await expect(answers(page)).toHaveCount(1);
    await conversationId(page);
    await expect(answers(page).first().locator('.searched')).toHaveCount(0); // a first question is searched for as typed

    await ask(page, 'and who may learn it?');
    await expect(answers(page)).toHaveCount(2);
    const followUp = answers(page).last();

    await expect(followUp).toContainText('[asked: What is the launch codename? and who may learn it?]'); // what the AI service was actually asked
    await expect(followUp.locator('.searched')).toHaveText('Searched for: What is the launch codename? and who may learn it?');
    await expect(page.getByRole('article', { name: 'You asked' }).last()).toContainText('and who may learn it?'); // the person's own words are what is kept as theirs
    await expect(page.getByRole('article', { name: 'You asked' }).last()).not.toContainText('Searched for');

    await page.reload(); // read back from the server, not from the page's memory
    await expect(answers(page)).toHaveCount(2);
    await expect(answers(page).last().locator('.searched')).toContainText('What is the launch codename? and who may learn it?');
    await expect(answers(page).first().locator('.searched')).toHaveCount(0);
  });

  test('a question that stands on its own is not rewritten, even in the middle of a conversation', async ({ page }) => {
    await register(page, newAccount('standalone'));
    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page.getByText(/Added plans\.md/)).toBeVisible();
    await openChat(page);
    await ask(page, 'What is the launch codename?');
    await expect(answers(page)).toHaveCount(1);
    await conversationId(page);

    await ask(page, 'What is the launch budget?');

    await expect(answers(page)).toHaveCount(2);
    await expect(answers(page).last()).toContainText('[asked: What is the launch budget?]');
    await expect(answers(page).last().locator('.searched')).toHaveCount(0);
  });

  test('a conversation started by someone else is not a source of context: a follow-up in a new conversation is searched for as typed', async ({ page }) => {
    await register(page, newAccount('fresh'));
    await uploadDocument(page, 'plans.md', PLANS);
    await expect(page.getByText(/Added plans\.md/)).toBeVisible();
    await openChat(page);
    await ask(page, 'What is the launch codename?');
    await expect(answers(page)).toHaveCount(1);
    await conversationId(page);

    await page.getByRole('link', { name: 'New conversation' }).click(); // a different conversation: the earlier one must not leak into it
    await ask(page, 'and who may learn it?');

    await expect(answers(page)).toHaveCount(1);
    await expect(answers(page).first()).toContainText('[asked: and who may learn it?]');
    await expect(answers(page).first().locator('.searched')).toHaveCount(0);
  });
});
