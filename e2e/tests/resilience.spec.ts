import { randomBytes } from 'node:crypto';

import { expect, test } from '@playwright/test';

import { PLANS, answer, ask, newAccount, openChat, register, uploadDocument } from './support';

async function readyToAsk(page: import('@playwright/test').Page, label: string): Promise<void> {
  await register(page, newAccount(label));
  await uploadDocument(page, 'plans.md', PLANS);
  await expect(page.getByText(/Added plans\.md/)).toBeVisible();
  await openChat(page);
}

test.describe('when things go wrong', () => {
  test('a rate-limited model shows what happened, the request id and the unanswered question, and Retry works', async ({ page }) => {
    await readyToAsk(page, 'retry');
    const question = `A flaky question ${randomBytes(3).toString('hex')}`; // the stand-in fails once per distinct text

    await ask(page, question);

    const error = page.getByRole('alert');
    await expect(error).toContainText('The AI service is busy or not reachable.');
    await expect(error).toContainText('Try again in about 10 seconds.');
    await expect(error).toContainText(/Request id [0-9a-f]{32}/);
    await expect(error).toContainText(question); // the question that was not answered
    await expect(page.getByRole('textbox', { name: 'Your question' })).toHaveValue(''); // and the box is empty: nothing is sent twice by accident
    await expect(page.getByRole('navigation', { name: 'Conversations' }).getByText('No conversations yet.')).toBeVisible(); // nothing was saved

    await error.getByRole('button', { name: 'Retry' }).click();

    await expect(answer(page)).toContainText(`[asked: ${question}]`);
    await expect(page).toHaveURL(/\/chat\/[0-9a-f-]{36}$/); // the first answer gives the conversation its address
    await expect(page.getByRole('alert')).toHaveCount(0);
  });

  test('Stop abandons the wait, puts the question back in the box, and saves nothing', async ({ page }) => {
    await readyToAsk(page, 'stop');
    const question = 'A slow question about the budget'; // the stand-in takes four seconds

    await ask(page, question);
    await expect(page.getByRole('status').filter({ hasText: 'Thinking' })).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Your question' })).toBeDisabled(); // locked while waiting
    await page.getByRole('button', { name: 'Stop' }).click();

    await expect(page.getByRole('textbox', { name: 'Your question' })).toHaveValue(question);
    await expect(page.getByRole('textbox', { name: 'Your question' })).toBeEnabled();
    await expect(page.getByRole('alert')).toHaveCount(0); // stopping is not a failure

    await page.waitForTimeout(5_500); // long enough for the service to have finished the call the browser walked away from
    await page.reload();
    await expect(page.getByRole('navigation', { name: 'Conversations' }).getByText('No conversations yet.')).toBeVisible();
  });
});

test.describe('a clarifying question', () => {
  test('the answer is sent to the service together with the original question, and the history keeps only what was typed', async ({ page }) => {
    await readyToAsk(page, 'clarify');

    await ask(page, 'What about that vague?', { agent: true });

    const asked = page.getByRole('article', { name: 'Needs one more detail' });
    await expect(asked).toContainText('Which topic do you mean?');
    await expect(page.getByRole('textbox', { name: 'Your answer' })).toBeVisible(); // the box has switched to answering
    await expect(page.getByText('Answering:')).toBeVisible();

    await ask(page, 'the budget');

    // the stand-in echoes what reached the AI service: the original question and the answer, joined
    await expect(answer(page)).toContainText('[asked: What about that vague? the budget]');
    const typed = page.getByRole('article', { name: 'You asked' });
    await expect(typed).toHaveText([/What about that vague\?/, /the budget/]); // what the person typed, not the combined text
    await expect(page.getByText('Answering:')).toHaveCount(0);
  });

  test('"Ask something else instead" sends the next question on its own', async ({ page }) => {
    await readyToAsk(page, 'else');
    await ask(page, 'What about that vague?', { agent: true });
    await expect(page.getByRole('article', { name: 'Needs one more detail' })).toBeVisible();

    await page.getByRole('button', { name: 'Ask something else instead' }).click();
    await expect(page.getByText('This will be asked as a new question.')).toBeVisible();
    await ask(page, 'What is the budget?');

    await expect(answer(page)).toContainText('[asked: What is the budget?]'); // not joined to the vague question
  });
});
