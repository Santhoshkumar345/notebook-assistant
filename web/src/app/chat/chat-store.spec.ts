import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { errorInterceptor } from '../api/api-error';
import type { AnswerStatus, AskResponse, ConversationOut, MessageOut } from '../api/models';
import { ChatStore } from './chat-store';

const AT = '2026-10-31T09:00:00+00:00';
const FROM_API = { 'X-Request-ID': 'req-1' };

const user = (id: number, text: string): MessageOut => ({ id, role: 'user', text, status: null, action: null, sources: [], createdAt: AT });
const assistant = (id: number, status: AnswerStatus, text: string): MessageOut => ({ id, role: 'assistant', text, status, action: 'retrieve', sources: [], createdAt: AT });
const conversation = (id: string, messages: MessageOut[]): ConversationOut => ({ id, title: 'A title', createdAt: AT, updatedAt: AT, messages });
const reply = (id: string, status: AnswerStatus = 'answered'): AskResponse => ({ conversationId: id, messageId: 2, status, answer: 'x', sources: [], action: 'retrieve', trace: [] });
const summary = (id: string) => ({ id, title: 'A title', messages: 2, createdAt: AT, updatedAt: AT });

const settle = () => new Promise<void>((resolve) => setTimeout(resolve));

describe('ChatStore', () => {
  let store: ChatStore;
  let http: HttpTestingController;

  async function next(method: string, url: string): Promise<TestRequest> {
    await settle();
    return http.expectOne({ method, url });
  }

  /** Answers the two reads that follow a finished ask: the conversation, then the list. */
  async function finishAsk(id: string, thread: ConversationOut) {
    (await next('GET', `/api/conversations/${id}`)).flush(thread);
    (await next('GET', '/api/conversations')).flush([summary(id)]);
  }

  /** Opens a conversation that ends with the given messages. */
  async function openWith(id: string, messages: MessageOut[]) {
    const opening = store.open(id);
    http.expectOne(`/api/conversations/${id}`).flush(conversation(id, messages));
    await opening;
  }

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([errorInterceptor])), provideHttpClientTesting()] });
    store = TestBed.inject(ChatStore);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  describe('the conversation list', () => {
    it('loads', async () => {
      const done = store.loadList();
      http.expectOne('/api/conversations').flush([summary('a'), summary('b')]);
      await done;

      expect(store.conversations().map((c) => c.id)).toEqual(['a', 'b']);
      expect(store.listLoaded()).toBe(true);
    });

    it('records a failure and keeps the old list', async () => {
      const first = store.loadList();
      http.expectOne('/api/conversations').flush([summary('a')]);
      await first;

      const second = store.loadList();
      http.expectOne('/api/conversations').flush({ title: 'x' }, { status: 503, statusText: 'x', headers: FROM_API });
      await second;

      expect(store.listError()?.kind).toBe('busy');
      expect(store.conversations().length).toBe(1);
    });
  });

  describe('opening a conversation', () => {
    it('shows an empty new conversation for null, without any request', async () => {
      await store.open(null);
      expect(store.messages()).toEqual([]);
      expect(store.activeId()).toBeNull();
    });

    it('loads the messages', async () => {
      await openWith('c-1', [user(1, 'Q'), assistant(2, 'answered', 'A')]);
      expect(store.activeId()).toBe('c-1');
      expect(store.messages().map((m) => m.text)).toEqual(['Q', 'A']);
    });

    it('does nothing when that conversation is already on screen', async () => {
      await openWith('c-1', [user(1, 'Q'), assistant(2, 'answered', 'A')]);
      await store.open('c-1'); // http.verify() fails if this asked the server again
    });

    it('ignores a slow answer for a conversation the person has already left', async () => {
      const a = store.open('a');
      const b = store.open('b');
      http.expectOne('/api/conversations/b').flush(conversation('b', [user(1, 'in b')]));
      http.expectOne('/api/conversations/a').flush(conversation('a', [user(1, 'in a')]));
      await Promise.all([a, b]);

      expect(store.messages().map((m) => m.text)).toEqual(['in b']);
    });

    it('reports a conversation that does not exist', async () => {
      const opening = store.open('gone');
      http.expectOne('/api/conversations/gone').flush({ title: 'No such conversation.' }, { status: 404, statusText: 'x', headers: FROM_API });
      await opening;

      expect(store.threadError()?.kind).toBe('not-found');
      expect(store.messages()).toEqual([]);
    });
  });

  describe('asking', () => {
    it('starts a conversation: sends the box text with the mode, shows it as pending, then reads the saved result back', async () => {
      store.draft.set('  Which store is used?  ');

      const done = store.send();
      expect(store.draft()).toBe('');
      expect(store.sending()?.text).toBe('Which store is used?');

      const post = await next('POST', '/api/ask');
      expect(post.request.body).toEqual({ question: 'Which store is used?', mode: 'pipeline', conversationId: null, answersClarification: false });
      post.flush(reply('c-9'));
      await finishAsk('c-9', conversation('c-9', [user(1, 'Which store is used?'), assistant(2, 'answered', 'Chroma.')]));

      expect(await done).toBe('c-9');
      expect(store.activeId()).toBe('c-9');
      expect(store.messages().length).toBe(2);
      expect(store.sending()).toBeNull(); // the pending question stays until the saved copy is on screen
      expect(store.conversations().map((c) => c.id)).toEqual(['c-9']);
    });

    it('continues the open conversation and uses the chosen mode', async () => {
      await openWith('c-1', [user(1, 'Q'), assistant(2, 'answered', 'A')]);
      store.setMode('agent');
      store.draft.set('And then?');

      const done = store.send();
      const post = await next('POST', '/api/ask');
      expect(post.request.body).toMatchObject({ conversationId: 'c-1', mode: 'agent', answersClarification: false });
      post.flush(reply('c-1'));
      await finishAsk('c-1', conversation('c-1', [user(1, 'Q'), assistant(2, 'answered', 'A'), user(3, 'And then?'), assistant(4, 'answered', 'B')]));
      await done;

      expect(store.messages().length).toBe(4);
    });

    it('ignores an empty box and a second send while one is in flight', async () => {
      store.draft.set('   ');
      expect(await store.send()).toBeNull();

      store.draft.set('first question');
      const first = store.send();
      store.draft.set('second question');
      expect(await store.send()).toBeNull();

      const post = http.expectOne({ method: 'POST', url: '/api/ask' }); // exactly one
      expect(post.request.body.question).toBe('first question');
      post.flush(reply('c-1'));
      await finishAsk('c-1', conversation('c-1', []));
      await first;
    });
  });

  describe('answering a clarifying question', () => {
    const waiting = [user(1, 'How should that be handled?'), assistant(2, 'clarify', 'Which one do you mean?')];

    it('is the default when the last turn is a clarifying question', async () => {
      await openWith('c-1', waiting);
      expect(store.pendingClarification()?.text).toBe('Which one do you mean?');
      expect(store.answering()).toBe(true);
    });

    it('does not apply when the last turn is an answer, or the conversation is new', async () => {
      await openWith('c-1', [user(1, 'Q'), assistant(2, 'answered', 'A')]);
      expect(store.answering()).toBe(false);
      await store.open(null);
      expect(store.answering()).toBe(false);
    });

    it('marks the request as an answer to the clarifying question', async () => {
      await openWith('c-1', waiting);
      store.draft.set('relative dates');

      const done = store.send();
      const post = await next('POST', '/api/ask');
      expect(post.request.body).toMatchObject({ question: 'relative dates', conversationId: 'c-1', answersClarification: true });
      post.flush(reply('c-1'));
      await finishAsk('c-1', conversation('c-1', [...waiting, user(3, 'relative dates'), assistant(4, 'answered', 'A')]));
      await done;

      expect(store.answering()).toBe(false); // answered now
    });

    it('lets the person ask something else instead, and back again', async () => {
      await openWith('c-1', waiting);

      store.setAskingSomethingElse(true);
      expect(store.answering()).toBe(false);
      store.draft.set('What is the embedding limit?');
      const done = store.send();
      const post = await next('POST', '/api/ask');
      expect(post.request.body.answersClarification).toBe(false);
      post.flush(reply('c-1'));
      await finishAsk('c-1', conversation('c-1', waiting));
      await done;

      store.setAskingSomethingElse(false);
      expect(store.answering()).toBe(true);
    });

    it('goes back to answering when another conversation is opened and this one is revisited', async () => {
      await openWith('c-1', waiting);
      store.setAskingSomethingElse(true);

      await store.open(null);
      await openWith('c-1', waiting);

      expect(store.answering()).toBe(true);
    });
  });

  describe('when the request fails', () => {
    it('shows the error with the question that was not answered, and keeps the box empty', async () => {
      store.draft.set('Which store is used?');
      const done = store.send();
      (await next('POST', '/api/ask')).flush({ title: 'The AI service is unavailable.' }, { status: 503, statusText: 'x', headers: { ...FROM_API, 'Retry-After': '10' } });
      expect(await done).toBeNull();

      expect(store.sendError()?.kind).toBe('busy');
      expect(store.sendError()?.requestId).toBe('req-1');
      expect(store.failedQuestion()).toBe('Which store is used?');
      expect(store.canRetry()).toBe(true);
      expect(store.sending()).toBeNull();
      expect(store.draft()).toBe('');
    });

    it('retries exactly the same request, including that it answers a clarifying question', async () => {
      await openWith('c-1', [user(1, 'Vague?'), assistant(2, 'clarify', 'Which?')]);
      store.draft.set('that one');
      const first = store.send();
      (await next('POST', '/api/ask')).flush({ title: 'x' }, { status: 504, statusText: 'x', headers: FROM_API });
      await first;

      const retry = store.retry();
      const post = await next('POST', '/api/ask');
      expect(post.request.body).toEqual({ question: 'that one', mode: 'pipeline', conversationId: 'c-1', answersClarification: true });
      post.flush(reply('c-1'));
      await finishAsk('c-1', conversation('c-1', []));
      await retry;

      expect(store.sendError()).toBeNull();
      expect(store.canRetry()).toBe(false);
    });

    it('can be dismissed, which also drops the retry', async () => {
      store.draft.set('Which store is used?');
      const done = store.send();
      (await next('POST', '/api/ask')).flush({ title: 'x' }, { status: 502, statusText: 'x', headers: FROM_API });
      await done;

      store.dismissSendError();

      expect(store.sendError()).toBeNull();
      expect(store.canRetry()).toBe(false);
      expect(await store.retry()).toBeNull();
    });

    it('reports a conversation deleted in the meantime as not found', async () => {
      await openWith('c-1', [user(1, 'Q'), assistant(2, 'answered', 'A')]);
      store.draft.set('And then?');
      const done = store.send();
      (await next('POST', '/api/ask')).flush({ title: 'No such conversation.' }, { status: 404, statusText: 'x', headers: FROM_API });
      await done;

      expect(store.sendError()?.kind).toBe('not-found');
    });
  });

  describe('stop', () => {
    it('cancels the request, puts the text back in the box and re-reads the list in case it had just been saved', async () => {
      store.draft.set('Which store is used?');
      const done = store.send();
      const post = http.expectOne({ method: 'POST', url: '/api/ask' });

      store.stop();

      expect(store.draft()).toBe('Which store is used?');
      expect(post.cancelled).toBe(true);
      (await next('GET', '/api/conversations')).flush([]);
      expect(await done).toBeNull();
      expect(store.sending()).toBeNull();
      expect(store.sendError()).toBeNull(); // a stop is not a failure
      expect(store.canRetry()).toBe(false);
    });

    it('re-reads the open conversation, because the answer may have been saved just before the cancel', async () => {
      await openWith('c-1', [user(1, 'Q'), assistant(2, 'answered', 'A')]);
      store.draft.set('And then?');
      const done = store.send();
      http.expectOne({ method: 'POST', url: '/api/ask' });

      store.stop();

      (await next('GET', '/api/conversations/c-1')).flush(conversation('c-1', [user(1, 'Q'), assistant(2, 'answered', 'A'), user(3, 'And then?'), assistant(4, 'answered', 'B')]));
      (await next('GET', '/api/conversations')).flush([summary('c-1')]);
      await done;

      expect(store.messages().length).toBe(4);
    });

    it('does not overwrite text the person typed meanwhile', async () => {
      store.draft.set('first');
      const done = store.send();
      http.expectOne({ method: 'POST', url: '/api/ask' });
      store.draft.set('typed while waiting');

      store.stop();
      (await next('GET', '/api/conversations')).flush([]);
      await done;

      expect(store.draft()).toBe('typed while waiting');
    });

    it('does nothing when nothing is in flight', () => {
      store.stop(); // no request, no error
      expect(store.sending()).toBeNull();
    });
  });

  describe('the question counter the header listens to', () => {
    it('goes up when a question is answered, fails, or is stopped', async () => {
      expect(store.asks()).toBe(0);

      store.draft.set('first question');
      const answered = store.send();
      (await next('POST', '/api/ask')).flush(reply('c-1'));
      await finishAsk('c-1', conversation('c-1', []));
      await answered;
      expect(store.asks()).toBe(1);

      store.draft.set('second question');
      const failed = store.send();
      (await next('POST', '/api/ask')).flush({ title: 'x' }, { status: 503, statusText: 'x', headers: FROM_API });
      await failed;
      expect(store.asks()).toBe(2);

      store.draft.set('third question');
      const stopped = store.send();
      http.expectOne({ method: 'POST', url: '/api/ask' });
      store.stop();
      (await next('GET', '/api/conversations/c-1')).flush(conversation('c-1', []));
      (await next('GET', '/api/conversations')).flush([]);
      await stopped;
      expect(store.asks()).toBe(3);
    });
  });

  describe('deleting', () => {
    it('deletes, re-reads the list, and says when the open conversation was the one removed', async () => {
      await openWith('c-1', [user(1, 'Q'), assistant(2, 'answered', 'A')]);

      const removing = store.remove('c-1');
      (await next('DELETE', '/api/conversations/c-1')).flush(null, { status: 204, statusText: 'No Content' });
      (await next('GET', '/api/conversations')).flush([]);

      expect(await removing).toBe(true);
      expect(store.activeId()).toBeNull();
      expect(store.messages()).toEqual([]);
    });

    it('leaves the open conversation alone when another one is deleted', async () => {
      await openWith('c-1', [user(1, 'Q'), assistant(2, 'answered', 'A')]);

      const removing = store.remove('c-2');
      (await next('DELETE', '/api/conversations/c-2')).flush(null, { status: 204, statusText: 'No Content' });
      (await next('GET', '/api/conversations')).flush([summary('c-1')]);

      expect(await removing).toBe(false);
      expect(store.activeId()).toBe('c-1');
    });

    it('treats "already gone" as done and shows any other failure', async () => {
      const gone = store.remove('c-2');
      (await next('DELETE', '/api/conversations/c-2')).flush({ title: 'x' }, { status: 404, statusText: 'x', headers: FROM_API });
      (await next('GET', '/api/conversations')).flush([]);
      await gone;
      expect(store.deleteError()).toBeNull();

      const failing = store.remove('c-3');
      (await next('DELETE', '/api/conversations/c-3')).flush({ title: 'x' }, { status: 500, statusText: 'x', headers: FROM_API });
      (await next('GET', '/api/conversations')).flush([]);
      await failing;
      expect(store.deleteError()?.kind).toBe('unknown');
    });
  });

  describe('the mode', () => {
    it('defaults to standard, and remembers the choice for next time', () => {
      expect(store.mode()).toBe('pipeline');

      store.setMode('agent');

      expect(store.mode()).toBe('agent');
      expect(localStorage.getItem('notebook.chat.mode')).toBe('agent');
    });

    it('starts from the remembered mode', () => {
      localStorage.setItem('notebook.chat.mode', 'agent');
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });

      expect(TestBed.inject(ChatStore).mode()).toBe('agent');
    });

    it('still works when storage is unavailable', () => {
      const blocked = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
        throw new Error('blocked');
      });

      expect(() => store.setMode('agent')).not.toThrow();
      expect(store.mode()).toBe('agent');
      blocked.mockRestore();
    });
  });
});
