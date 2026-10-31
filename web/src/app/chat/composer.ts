import { Component, computed, inject, output } from '@angular/core';

import type { AskMode } from '../api/models';
import { ChatStore } from './chat-store';
import { MAX_QUESTION_CHARS, checkQuestion } from './question-checks';

const MODE_HELP: Record<AskMode, string> = {
  pipeline: 'Searches your documents and answers with sources.',
  agent: 'Can also ask you a clarifying question or summarize a whole document. Uses more model calls, so it is slower.',
};

@Component({
  selector: 'app-composer',
  templateUrl: './composer.html',
  styleUrl: './composer.css',
})
export class Composer {
  protected readonly store = inject(ChatStore);

  /** The person asked to send. The page does the sending, so it can move to the new conversation afterwards. */
  readonly submitted = output<void>();

  protected readonly max = MAX_QUESTION_CHARS;
  protected readonly modeHelp = computed(() => MODE_HELP[this.store.mode()]);
  protected readonly length = computed(() => this.store.draft().trim().length);
  /** Only a message that is too long is worth showing: a short one just keeps Send disabled. */
  protected readonly tooLong = computed(() => (this.length() > this.max ? checkQuestion(this.store.draft()) : null));
  protected readonly canSend = computed(() => this.store.sending() === null && checkQuestion(this.store.draft()) === null);

  protected onInput(event: Event): void {
    this.store.draft.set((event.target as HTMLTextAreaElement).value);
  }

  protected onKeydown(event: KeyboardEvent): void {
    // Enter sends, Shift+Enter makes a new line; Enter while an input method (IME) is composing text is not "send".
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      this.submit();
    }
  }

  protected submit(): void {
    if (this.canSend()) this.submitted.emit();
  }

  protected setMode(mode: AskMode): void {
    this.store.setMode(mode);
  }
}
