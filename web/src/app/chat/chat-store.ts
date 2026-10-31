import { Injectable, computed, inject, signal } from '@angular/core';
import { Subject, firstValueFrom, takeUntil } from 'rxjs';

import { ApiClient } from '../api/api-client';
import type { ApiError } from '../api/api-error';
import { asApiError } from '../api/api-error';
import type { AskMode, ConversationOut, ConversationSummary, MessageOut } from '../api/models';

const MODE_KEY = 'notebook.chat.mode';

/** What was sent, kept so that Retry sends exactly the same thing. */
interface Draft {
  text: string;
  mode: AskMode;
  answersClarification: boolean;
}

/**
 * The chat screen's state. The server keeps the history; this holds what is on screen and the one request in flight.
 * A request that fails or is stopped is not part of the conversation (the API saves an exchange only when it is finished),
 * so after a failure the question is kept for Retry, and after Stop the text goes back into the box.
 */
@Injectable({ providedIn: 'root' })
export class ChatStore {
  private readonly api = inject(ApiClient);
  private readonly stopRequested = new Subject<void>();
  private readonly askCount = signal(0);
  private readonly failed = signal<Draft | null>(null);

  readonly conversations = signal<ConversationSummary[]>([]);
  readonly listLoaded = signal(false);
  readonly listError = signal<ApiError | null>(null);
  readonly deleteError = signal<ApiError | null>(null);

  readonly activeId = signal<string | null>(null);
  readonly thread = signal<ConversationOut | null>(null);
  readonly threadLoading = signal(false);
  readonly threadError = signal<ApiError | null>(null);

  /** The question being answered right now, and when it was sent. */
  readonly sending = signal<{ text: string; since: number } | null>(null);
  readonly sendError = signal<ApiError | null>(null);
  /** The question whose request failed, shown with the error so the person can see what was not answered. */
  readonly failedQuestion = computed(() => this.failed()?.text ?? null);
  readonly canRetry = computed(() => this.failed() !== null);

  /** Goes up whenever a question stops being in flight (answered, failed or stopped), so the header can re-check that the system is healthy. */
  readonly asks = this.askCount.asReadonly();

  /** What is typed in the box. Lives here so Stop can put the text back. */
  readonly draft = signal('');
  readonly mode = signal<AskMode>(readMode());

  private readonly askingSomethingElse = signal(false);

  readonly messages = computed<MessageOut[]>(() => this.thread()?.messages ?? []);

  /** The clarifying question the conversation is waiting on: the last turn, if it is one. */
  readonly pendingClarification = computed<MessageOut | null>(() => {
    const last = this.messages().at(-1);
    return last?.role === 'assistant' && last.status === 'clarify' ? last : null;
  });

  /** True when the next message is sent as the answer to that clarifying question (the person can opt out and ask something else). */
  readonly answering = computed(() => this.pendingClarification() !== null && !this.askingSomethingElse());

  setMode(mode: AskMode): void {
    this.mode.set(mode);
    try {
      localStorage.setItem(MODE_KEY, mode);
    } catch {
      /* storage can be blocked or full: the choice then lasts for this visit only */
    }
  }

  setAskingSomethingElse(value: boolean): void {
    this.askingSomethingElse.set(value);
  }

  async loadList(): Promise<void> {
    try {
      this.conversations.set(await firstValueFrom(this.api.conversations()));
      this.listError.set(null);
      this.listLoaded.set(true);
    } catch (error) {
      this.listError.set(asApiError(error));
    }
  }

  /** Shows a conversation, or an empty new one for null. Does nothing if it is already the one on screen. */
  async open(id: string | null): Promise<void> {
    if (id === this.activeId() && (id === null || this.thread() !== null)) return;

    this.activeId.set(id);
    this.thread.set(null);
    this.threadError.set(null);
    this.askingSomethingElse.set(false);
    this.dismissSendError();
    if (id !== null) await this.loadThread(id);
  }

  /** Sends the box's text. Resolves to the conversation's id when the answer arrived and was saved, otherwise null. */
  async send(): Promise<string | null> {
    const text = this.draft().trim();
    if (this.sending() || text === '') return null;
    this.draft.set('');
    return this.run({ text, mode: this.mode(), answersClarification: this.answering() });
  }

  async retry(): Promise<string | null> {
    const draft = this.failed();
    if (this.sending() || draft === null) return null;
    return this.run(draft);
  }

  /** Abandons the request in flight and puts the text back in the box. */
  stop(): void {
    const current = this.sending();
    if (current === null) return;
    if (this.draft() === '') this.draft.set(current.text);
    this.stopRequested.next();
  }

  dismissSendError(): void {
    this.sendError.set(null);
    this.failed.set(null);
  }

  /** Resolves to true when the conversation on screen was the one deleted. */
  async remove(id: string): Promise<boolean> {
    this.deleteError.set(null);
    try {
      await firstValueFrom(this.api.deleteConversation(id));
    } catch (error) {
      const failure = asApiError(error);
      if (failure.kind !== 'not-found') this.deleteError.set(failure); // already gone is the outcome that was wanted
    }
    await this.loadList();

    if (this.activeId() !== id) return false;
    this.activeId.set(null);
    this.thread.set(null);
    return true;
  }

  private async run(draft: Draft): Promise<string | null> {
    this.dismissSendError();
    this.sending.set({ text: draft.text, since: Date.now() });
    const conversationId = this.activeId();
    try {
      const response = await firstValueFrom(
        this.api.ask({ question: draft.text, mode: draft.mode, conversationId, answersClarification: draft.answersClarification }).pipe(takeUntil(this.stopRequested)),
        { defaultValue: null },
      );

      if (response === null) {
        // Stopped. The server may have finished and saved it just before the request was cancelled, so read the truth back.
        await Promise.all([conversationId === null ? Promise.resolve() : this.loadThread(conversationId), this.loadList()]);
        return null;
      }

      this.activeId.set(response.conversationId);
      this.askingSomethingElse.set(false);
      await Promise.all([this.loadThread(response.conversationId), this.loadList()]);
      return response.conversationId;
    } catch (error) {
      this.sendError.set(asApiError(error));
      this.failed.set(draft);
      return null;
    } finally {
      this.sending.set(null);
      this.askCount.update((n) => n + 1);
    }
  }

  private async loadThread(id: string): Promise<void> {
    this.threadLoading.set(true);
    try {
      const thread = await firstValueFrom(this.api.conversation(id));
      if (this.activeId() === id) this.thread.set(thread); // ignore an answer for a conversation the person has since left
    } catch (error) {
      if (this.activeId() === id) this.threadError.set(asApiError(error));
    } finally {
      this.threadLoading.set(false);
    }
  }
}

function readMode(): AskMode {
  try {
    return localStorage.getItem(MODE_KEY) === 'agent' ? 'agent' : 'pipeline';
  } catch {
    return 'pipeline';
  }
}
