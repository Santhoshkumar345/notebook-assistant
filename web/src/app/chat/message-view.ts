import { Component, input } from '@angular/core';

import type { AnswerStatus, MessageOut } from '../api/models';

/**
 * What each outcome is called for a screen reader and a heading. A Record over the status union: if the API gains a status,
 * this stops compiling until the page says what to show for it.
 */
export const STATUS_LABEL: Record<AnswerStatus, string> = {
  answered: 'Answer',
  not_found: 'Not found',
  uncited: 'No supported answer',
  clarify: 'Needs one more detail',
};

@Component({
  selector: 'app-message-view',
  templateUrl: './message-view.html',
  styleUrl: './message-view.css',
})
export class MessageView {
  readonly message = input.required<MessageOut>();

  protected label(status: AnswerStatus | null): string {
    return status === null ? 'Assistant' : STATUS_LABEL[status];
  }
}
