import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { errorInterceptor } from '../api/api-error';
import type { AnswerStatus, ConversationOut, DocumentInfo, MessageOut } from '../api/models';
import { ChatPage } from './chat-page';

const AT = '2026-10-31T09:00:00+00:00';
const FROM_API = { 'X-Request-ID': 'req-42' };

const user = (id: number, text: string): MessageOut => ({ id, role: 'user', text, status: null, action: null, sources: [], searchedFor: null, createdAt: AT });
const assistant = (id: number, status: AnswerStatus, text: string, sources: MessageOut['sources'] = []): MessageOut => ({ id, role: 'assistant', text, status, action: 'retrieve', sources, searchedFor: null, createdAt: AT });
const thread = (id: string, messages: MessageOut[], title = 'Which store is used?'): ConversationOut => ({ id, title, createdAt: AT, updatedAt: AT, messages });
const doc = (name: string, status: DocumentInfo['status'] = 'Indexed'): DocumentInfo => ({ name, chars: 10, chunks: 1, status, error: null, createdAt: AT, updatedAt: AT });
const summary = (id: string) => ({ id, title: 'Which store is used?', messages: 2, createdAt: AT, updatedAt: AT });
const reply = (id: string) => ({ conversationId: id, messageId: 2, status: 'answered' as const, answer: 'x', sources: [], action: 'retrieve', trace: [], searchedFor: null });

const settle = () => new Promise<void>((resolve) => setTimeout(resolve));

describe('ChatPage', () => {
  let fixture: ComponentFixture<ChatPage>;
  let http: HttpTestingController;
  let navigate: ReturnType<typeof vi.spyOn>;

  const root = () => fixture.nativeElement as HTMLElement;
  const text = () => root().textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const box = () => root().querySelector('textarea') as HTMLTextAreaElement;
  const button = (label: string) =>
    Array.from(root().querySelectorAll('button')).find((b) => b.textContent?.trim() === label || b.getAttribute('aria-label') === label) as HTMLButtonElement | undefined;

  async function render() {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function next(method: string, url: string): Promise<TestRequest> {
    await settle();
    return http.expectOne({ method, url });
  }

  /** Opens the page and answers the reads it makes on arrival. `conversation` is the open conversation, or 'missing' for a 404. */
  async function open(options: { id?: string; conversation?: ConversationOut | 'missing'; documents?: DocumentInfo[] } = {}) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([errorInterceptor])), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(ChatPage);
    if (options.id) fixture.componentRef.setInput('id', options.id);
    fixture.detectChanges();

    http.expectOne({ method: 'GET', url: '/api/conversations' }).flush([]);
    http.expectOne({ method: 'GET', url: '/api/documents' }).flush(options.documents ?? [doc('a.md')]);
    if (options.id) {
      const request = http.expectOne({ method: 'GET', url: `/api/conversations/${options.id}` });
      if (options.conversation === 'missing') request.flush({ title: 'No such conversation.' }, { status: 404, statusText: 'x', headers: FROM_API });
      else request.flush(options.conversation ?? thread(options.id, []));
    }
    await settle();
    await render();
  }

  async function type(value: string) {
    box().value = value;
    box().dispatchEvent(new Event('input'));
    await render();
  }

  const clickAsk = () => (root().querySelector('button[type=submit]') as HTMLButtonElement).click();

  afterEach(() => http.verify());

  it('greets a new conversation and says what to expect', async () => {
    await open();

    expect(text()).toContain('New conversation');
    expect(text()).toContain('Ask your documents');
    expect(text()).toContain('you are told so instead of getting a guess');
  });

  describe('asking a question', () => {
    it('shows the question while it is answered, then the answer with its sources, and gives the conversation an address', async () => {
      await open();
      await type('Which store is used?');

      clickAsk();
      await settle();
      await render();
      expect(root().querySelector('.pending')?.textContent).toContain('Which store is used?');
      expect(text()).toContain('Thinking…');
      expect(box().value).toBe('');

      http.expectOne({ method: 'POST', url: '/api/ask' }).flush(reply('c-1'));
      (await next('GET', '/api/conversations/c-1')).flush(
        thread('c-1', [user(1, 'Which store is used?'), assistant(2, 'answered', 'Chroma.', [{ id: 'a.md#0', source: 'a.md', heading: 'Setup', snippet: 'Chroma is the store.' }])]),
      );
      (await next('GET', '/api/conversations')).flush([summary('c-1')]);
      await settle();
      await render();

      expect(root().querySelector('.pending')).toBeNull();
      expect(text()).toContain('Chroma.');
      expect(text()).toContain('a.md');
      expect(text()).not.toContain('Thinking…');
      expect(navigate).toHaveBeenCalledWith(['/chat', 'c-1']);
    });

    it('does not move to a new address when the conversation already has one', async () => {
      await open({ id: 'c-1', conversation: thread('c-1', [user(1, 'Q'), assistant(2, 'answered', 'A')]) });
      await type('And then?');

      clickAsk();
      http.expectOne({ method: 'POST', url: '/api/ask' }).flush(reply('c-1'));
      (await next('GET', '/api/conversations/c-1')).flush(thread('c-1', [user(1, 'Q'), assistant(2, 'answered', 'A'), user(3, 'And then?'), assistant(4, 'answered', 'B')]));
      (await next('GET', '/api/conversations')).flush([summary('c-1')]);
      await settle();

      expect(navigate).not.toHaveBeenCalled();
    });

    it('shows a conversation that is already there, with its title', async () => {
      await open({ id: 'c-1', conversation: thread('c-1', [user(1, 'Which store is used?'), assistant(2, 'answered', 'Chroma.')], 'Vector store question') });

      expect(root().querySelector('h1')?.textContent).toBe('Vector store question');
      expect(root().querySelectorAll('app-message-view').length).toBe(2);
    });
  });

  describe('a clarifying question', () => {
    const waiting = thread('c-1', [user(1, 'How should that be handled?'), assistant(2, 'clarify', 'Which one do you mean?')]);

    it('is shown as the assistant asking, with the box switched to answering it', async () => {
      await open({ id: 'c-1', conversation: waiting });

      expect(text()).toContain('Needs one more detail');
      expect(text()).toContain('Which one do you mean?');
      expect(text()).toContain('Answering:');
      expect(button('Send answer')).toBeDefined();
    });

    it('sends the answer marked as one, so the AI service gets the original question with it', async () => {
      await open({ id: 'c-1', conversation: waiting });
      await type('relative dates');

      clickAsk();
      await settle();
      const post = http.expectOne({ method: 'POST', url: '/api/ask' });
      expect(post.request.body).toEqual({ question: 'relative dates', mode: 'pipeline', conversationId: 'c-1', answersClarification: true });

      post.flush(reply('c-1'));
      (await next('GET', '/api/conversations/c-1')).flush(thread('c-1', [...waiting.messages, user(3, 'relative dates'), assistant(4, 'answered', 'Inject the current date.')]));
      (await next('GET', '/api/conversations')).flush([summary('c-1')]);
      await settle();
      await render();

      expect(text()).toContain('Inject the current date.');
      expect(text()).not.toContain('Answering:'); // nothing is waiting any more
    });

    it('sends a new question as a new question when the person chooses to ask something else', async () => {
      await open({ id: 'c-1', conversation: waiting });
      button('Ask something else instead')!.click();
      await render();
      await type('What is the embedding limit?');

      clickAsk();
      await settle();

      const post = http.expectOne({ method: 'POST', url: '/api/ask' });
      expect(post.request.body.answersClarification).toBe(false);
      post.flush(reply('c-1')); // let the store finish its reads below
      (await next('GET', '/api/conversations/c-1')).flush(waiting);
      (await next('GET', '/api/conversations')).flush([summary('c-1')]);
      await settle();
    });
  });

  describe('when the request fails', () => {
    it('shows what went wrong, the request id and the question that was not answered, then retries on request', async () => {
      await open();
      await type('Which store is used?');

      clickAsk();
      await settle();
      http.expectOne({ method: 'POST', url: '/api/ask' }).flush({ title: 'The AI service is unavailable.' }, { status: 503, statusText: 'x', headers: { ...FROM_API, 'Retry-After': '10' } });
      await settle();
      await render();

      expect(text()).toContain('The AI service is busy or not reachable.');
      expect(text()).toContain('about 10 seconds');
      expect(text()).toContain('req-42');
      expect(root().querySelector('.banner.error q')?.textContent).toBe('Which store is used?');
      expect(root().querySelector('.pending')).toBeNull();

      button('Retry')!.click();
      await settle();
      const retried = http.expectOne({ method: 'POST', url: '/api/ask' });
      expect(retried.request.body.question).toBe('Which store is used?');
      retried.flush(reply('c-1'));
      (await next('GET', '/api/conversations/c-1')).flush(thread('c-1', [user(1, 'Which store is used?'), assistant(2, 'answered', 'Chroma.')]));
      (await next('GET', '/api/conversations')).flush([summary('c-1')]);
      await settle();
      await render();

      expect(text()).not.toContain('Not answered');
      expect(text()).toContain('Chroma.');
      expect(navigate).toHaveBeenCalledWith(['/chat', 'c-1']); // a retried first question also gets its address
    });

    it('can be dismissed', async () => {
      await open();
      await type('Which store is used?');
      clickAsk();
      await settle();
      http.expectOne({ method: 'POST', url: '/api/ask' }).flush({ title: 'x' }, { status: 502, statusText: 'x', headers: FROM_API });
      await settle();
      await render();

      button('Dismiss')!.click();
      await render();

      expect(root().querySelector('.banner.error')).toBeNull();
    });
  });

  describe('stop', () => {
    it('abandons the wait and puts the question back in the box', async () => {
      await open();
      await type('Which store is used?');
      clickAsk();
      await settle();
      await render();
      const post = http.expectOne({ method: 'POST', url: '/api/ask' });

      button('Stop')!.click();
      expect(post.cancelled).toBe(true);
      (await next('GET', '/api/conversations')).flush([]);
      await settle();
      await render();

      expect(box().value).toBe('Which store is used?');
      expect(root().querySelector('.pending')).toBeNull();
      expect(root().querySelector('.banner.error')).toBeNull();
    });
  });

  describe('the hint about documents', () => {
    it('appears when no document is searchable yet, with a way to add one', async () => {
      await open({ documents: [doc('a.md', 'Failed')] });

      expect(text()).toContain('No document is searchable yet');
      expect(root().querySelector('a[href="/documents"]')).not.toBeNull();
    });

    it('appears when there are no documents at all', async () => {
      await open({ documents: [] });
      expect(text()).toContain('No document is searchable yet');
    });

    it('is absent once something is searchable', async () => {
      await open({ documents: [doc('a.md', 'Failed'), doc('b.md')] });
      expect(text()).not.toContain('No document is searchable yet');
    });
  });

  it('says so, with a way out, when the conversation in the address does not exist', async () => {
    await open({ id: 'gone', conversation: 'missing' });

    expect(text()).toContain('That item no longer exists');
    expect(text()).toContain('req-42');
    expect(root().querySelector('.thread a[href="/chat"]')?.textContent).toContain('Start a new conversation');
  });

  it('opens and closes the conversation list on a narrow screen', async () => {
    await open();
    const toggle = button('Conversations')!;
    const panel = root().querySelector('aside')!;

    expect(panel.classList.contains('open')).toBe(false);
    toggle.click();
    await render();
    expect(panel.classList.contains('open')).toBe(true);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
  });
});
