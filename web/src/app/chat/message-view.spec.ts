import { ComponentFixture, TestBed } from '@angular/core/testing';

import type { AnswerStatus, MessageOut } from '../api/models';
import { MessageView, STATUS_LABEL } from './message-view';

const AT = '2026-10-31T09:00:00+00:00';
const assistant = (status: AnswerStatus, text: string, sources: MessageOut['sources'] = []): MessageOut => ({ id: 2, role: 'assistant', text, status, action: 'retrieve', sources, createdAt: AT });

describe('MessageView', () => {
  let fixture: ComponentFixture<MessageView>;
  const root = () => fixture.nativeElement as HTMLElement;
  const text = () => root().textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function show(message: MessageOut) {
    fixture = TestBed.createComponent(MessageView);
    fixture.componentRef.setInput('message', message);
    await fixture.whenStable();
  }

  it('shows what the person asked', async () => {
    await show({ id: 1, role: 'user', text: 'Which store is used?', status: null, action: null, sources: [], createdAt: AT });

    expect(text()).toContain('You');
    expect(text()).toContain('Which store is used?');
  });

  it('shows an answer with its sources, each with the document, the heading and the passage behind a disclosure', async () => {
    await show(
      assistant('answered', 'Chroma.', [
        { id: 'a.md#0', source: 'a.md', heading: 'Setup', snippet: 'Chroma is the vector store.' },
        { id: 'b.md#3', source: 'b.md', heading: '', snippet: 'Second passage.' },
      ]),
    );

    expect(root().querySelector('.text')?.textContent).toBe('Chroma.');
    const items = root().querySelectorAll('.sources li');
    expect(items.length).toBe(2);
    expect(items[0].querySelector('summary')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('a.md · Setup');
    expect(items[0].querySelector('details')?.open).toBe(false);
    expect(items[0].querySelector('blockquote')?.textContent).toBe('Chroma is the vector store.');
    expect(items[1].querySelector('summary')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('b.md'); // no heading, no stray separator
  });

  it('does not render an empty sources section for an answer without sources', async () => {
    await show(assistant('answered', 'Chroma.'));
    expect(root().querySelector('.sources')).toBeNull();
  });

  it('says plainly that the documents do not contain the answer', async () => {
    await show(assistant('not_found', ''));

    expect(text()).toContain('Not found');
    expect(text()).toContain('The documents do not contain an answer');
    expect(root().querySelector('.sources')).toBeNull();
  });

  it('explains an unsupported answer without showing any of it', async () => {
    await show(assistant('uncited', 'a draft that must not be shown'));

    expect(text()).toContain('could not be backed by a source');
    expect(text()).not.toContain('a draft that must not be shown');
  });

  it('shows a clarifying question as the assistant asking for one more detail', async () => {
    await show(assistant('clarify', 'Which limit do you mean?'));

    expect(text()).toContain('Needs one more detail');
    expect(text()).toContain('Which limit do you mean?');
  });

  it('labels every status for a screen reader, and has a label for each one the API can send', async () => {
    expect(Object.keys(STATUS_LABEL).sort()).toEqual(['answered', 'clarify', 'not_found', 'uncited']);
    await show(assistant('not_found', ''));
    expect(root().querySelector('article')?.getAttribute('aria-label')).toBe('Not found');
  });

  it('treats text as text, never as markup', async () => {
    await show(assistant('answered', '<img src=x onerror="window.pwned=1"><b>bold</b>'));

    expect(root().querySelector('img')).toBeNull();
    expect(root().querySelector('b')).toBeNull();
    expect(root().querySelector('.text')?.textContent).toBe('<img src=x onerror="window.pwned=1"><b>bold</b>');
  });
});
