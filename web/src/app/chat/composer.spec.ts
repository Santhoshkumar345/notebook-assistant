import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import type { MessageOut } from '../api/models';
import { ChatStore } from './chat-store';
import { Composer } from './composer';

const AT = '2026-10-31T09:00:00+00:00';
const clarifying: MessageOut[] = [
  { id: 1, role: 'user', text: 'How should that be handled?', status: null, action: null, sources: [], searchedFor: null, createdAt: AT },
  { id: 2, role: 'assistant', text: 'Which one do you mean?', status: 'clarify', action: 'clarify', sources: [], searchedFor: null, createdAt: AT },
];

describe('Composer', () => {
  let fixture: ComponentFixture<Composer>;
  let store: ChatStore;
  let submitted: number;

  const root = () => fixture.nativeElement as HTMLElement;
  const box = () => root().querySelector('textarea') as HTMLTextAreaElement;
  const send = () => Array.from(root().querySelectorAll('button')).find((b) => b.type === 'submit') as HTMLButtonElement;
  const text = () => root().textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function render() {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function type(value: string) {
    box().value = value;
    box().dispatchEvent(new Event('input'));
    await render();
  }

  function press(init: KeyboardEventInit) {
    const event = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init });
    box().dispatchEvent(event);
    return event;
  }

  beforeEach(async () => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    store = TestBed.inject(ChatStore);
    fixture = TestBed.createComponent(Composer);
    submitted = 0;
    fixture.componentInstance.submitted.subscribe(() => submitted++);
    await render();
  });

  it('keeps Ask disabled until there is a question of at least three characters', async () => {
    expect(send().disabled).toBe(true);
    await type('hi');
    expect(send().disabled).toBe(true);
    await type('hi!');
    expect(send().disabled).toBe(false);
  });

  it('sends on Enter, and lets Shift+Enter add a line instead', async () => {
    await type('Which store is used?');

    const shifted = press({ key: 'Enter', shiftKey: true });
    expect(submitted).toBe(0);
    expect(shifted.defaultPrevented).toBe(false); // the new line is allowed

    const plain = press({ key: 'Enter' });
    expect(submitted).toBe(1);
    expect(plain.defaultPrevented).toBe(true); // and no stray new line is typed
  });

  it('does not treat Enter inside an input-method composition as a send', async () => {
    await type('Which store is used?');
    press({ key: 'Enter', isComposing: true });
    expect(submitted).toBe(0);
  });

  it('does not send an invalid question even with Enter', async () => {
    await type('hi');
    press({ key: 'Enter' });
    expect(submitted).toBe(0);
  });

  it('shows the count, and flags a question that is too long and blocks sending it', async () => {
    await type('x'.repeat(1001));

    expect(text()).toContain('1001 / 1000');
    expect(text()).toContain('The limit is 1,000');
    expect(box().getAttribute('aria-invalid')).toBe('true');
    expect(send().disabled).toBe(true);
  });

  it('disables the box, the mode and Ask while an answer is awaited', async () => {
    await type('Which store is used?');
    store.sending.set({ text: 'Which store is used?', since: Date.now() });
    await render();

    expect(box().disabled).toBe(true);
    expect(send().disabled).toBe(true);
    expect((root().querySelector('fieldset') as HTMLFieldSetElement).disabled).toBe(true);
  });

  describe('mode', () => {
    it('offers Standard and Agent, explains each, and remembers the choice', async () => {
      expect(text()).toContain('Searches your documents and answers with sources.');

      (root().querySelector('input[value=agent]') as HTMLInputElement).click();
      await render();

      expect(store.mode()).toBe('agent');
      expect(text()).toContain('clarifying question');
      expect(text()).toContain('slower');
    });
  });

  describe('when a clarifying question is waiting', () => {
    beforeEach(async () => {
      store.thread.set({ id: 'c-1', title: 't', createdAt: AT, updatedAt: AT, messages: clarifying });
      await render();
    });

    it('says what is being answered and changes the labels', () => {
      expect(text()).toContain('Answering:');
      expect(root().querySelector('.answering q')?.textContent).toBe('Which one do you mean?'); // the quotation marks are drawn by the q element
      expect(root().querySelector('label.sr-only')?.textContent).toBe('Your answer');
      expect(send().textContent).toContain('Send answer');
    });

    it('lets the person ask something else instead, and go back', async () => {
      (Array.from(root().querySelectorAll('button')).find((b) => b.textContent?.includes('Ask something else instead')) as HTMLButtonElement).click();
      await render();

      expect(text()).toContain('This will be asked as a new question.');
      expect(send().textContent).toContain('Ask');
      expect(send().textContent).not.toContain('Send answer');

      (Array.from(root().querySelectorAll('button')).find((b) => b.textContent?.includes('Answer the question above instead')) as HTMLButtonElement).click();
      await render();
      expect(text()).toContain('Answering:');
    });
  });

  it('shows no answering banner in an ordinary conversation', () => {
    expect(text()).not.toContain('Answering');
    expect(text()).not.toContain('new question');
  });
});
