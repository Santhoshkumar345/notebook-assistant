import { Component, ElementRef, afterRenderEffect, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { Router, RouterLink } from '@angular/router';

import { describeError } from '../api/api-error';
import { DocumentsStore } from '../documents/documents-store';
import { ChatStore } from './chat-store';
import { Composer } from './composer';
import { ConversationList } from './conversation-list';
import { MessageView } from './message-view';
import { ThinkingIndicator } from './thinking-indicator';

@Component({
  selector: 'app-chat-page',
  imports: [RouterLink, Composer, ConversationList, MessageView, ThinkingIndicator],
  templateUrl: './chat-page.html',
  styleUrl: './chat-page.css',
})
export class ChatPage {
  protected readonly store = inject(ChatStore);
  private readonly documents = inject(DocumentsStore);
  private readonly router = inject(Router);
  protected readonly describeError = describeError;

  /** The conversation in the address (/chat/:id); bound from the route. Absent on /chat, which is a new conversation. */
  readonly id = input<string | undefined>();

  /** On a narrow screen the conversation list is a panel the person opens. */
  protected readonly listOpen = signal(false);

  /** Not shown until the document list has loaded, so the hint does not flash for people who do have documents. */
  protected readonly nothingSearchable = computed(() => this.documents.loaded() && !this.documents.documents().some((d) => d.status === 'Indexed'));

  private readonly thread = viewChild<ElementRef<HTMLElement>>('thread');

  constructor() {
    effect(() => {
      const id = this.id() ?? null;
      untracked(() => {
        this.listOpen.set(false);
        void this.store.open(id);
      });
    });

    void this.store.loadList();
    if (!this.documents.loaded()) void this.documents.load();

    // Keep the newest turn in view as messages arrive or the pending question appears.
    afterRenderEffect(() => {
      this.store.messages();
      this.store.sending();
      this.store.sendError();
      const element = this.thread()?.nativeElement;
      if (element) element.scrollTop = element.scrollHeight;
    });
  }

  protected async submit(): Promise<void> {
    await this.moveToConversation(await this.store.send());
  }

  protected async submitRetry(): Promise<void> {
    await this.moveToConversation(await this.store.retry());
  }

  /** The first answer in a new conversation gives it an address. */
  private async moveToConversation(id: string | null): Promise<void> {
    if (id !== null && id !== this.id()) await this.router.navigate(['/chat', id]);
  }
}
