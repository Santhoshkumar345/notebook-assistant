import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';

import { describeError } from '../api/api-error';
import { ChatStore } from './chat-store';

@Component({
  selector: 'app-conversation-list',
  imports: [RouterLink, DatePipe],
  templateUrl: './conversation-list.html',
  styleUrl: './conversation-list.css',
})
export class ConversationList {
  protected readonly store = inject(ChatStore);
  private readonly router = inject(Router);
  protected readonly describeError = describeError;

  /** The conversation waiting for the person to confirm its deletion. */
  protected readonly confirmingId = signal<string | null>(null);

  protected async confirmDelete(id: string): Promise<void> {
    this.confirmingId.set(null);
    if (await this.store.remove(id)) await this.router.navigate(['/chat']);
  }
}
