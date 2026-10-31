import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { merge, skip, switchMap, timer } from 'rxjs';

import { ApiClient } from '../api/api-client';
import type { Health } from '../api/models';
import { ChatStore } from '../chat/chat-store';
import { DocumentsStore } from '../documents/documents-store';

export const HEALTH_POLL_MS = 30_000;

/** Whether the system can answer questions right now: the API, and the AI service behind it. Re-checked every 30 seconds. */
@Component({
  selector: 'app-health-badge',
  template: `
    <span class="health" [class]="'health ' + health().state" role="status">
      <span class="dot" aria-hidden="true"></span>
      {{ label() }}
    </span>
  `,
  styles: `
    .health {
      display: inline-flex;
      align-items: center;
      gap: 0.4rem;
      font-size: 0.875rem;
      color: var(--muted);
    }
    .dot {
      width: 0.6rem;
      height: 0.6rem;
      border-radius: 50%;
      background: var(--muted);
    }
    .ok .dot {
      background: var(--ok-fg);
    }
    .degraded .dot {
      background: var(--warn-fg);
    }
    .down .dot {
      background: var(--bad-fg);
    }
  `,
})
export class HealthBadge {
  private readonly api = inject(ApiClient);
  private readonly documents = inject(DocumentsStore);
  private readonly chat = inject(ChatStore);

  protected readonly health = signal<Health>({ state: 'checking' });

  constructor() {
    // Every 30 seconds, and straight away after a document changes or a question finishes: a failed question is the moment
    // the AI service most likely just went away.
    const afterWrite = toObservable(this.documents.writes).pipe(skip(1));
    const afterAsk = toObservable(this.chat.asks).pipe(skip(1));
    merge(timer(0, HEALTH_POLL_MS), afterWrite, afterAsk)
      .pipe(
        switchMap(() => this.api.health()),
        takeUntilDestroyed(),
      )
      .subscribe((health) => this.health.set(health));
  }

  protected label(): string {
    const health = this.health();
    switch (health.state) {
      case 'checking':
        return 'Checking…';
      case 'ok': {
        // The document count is the signed-in person's own (from their documents), never a figure for the whole system.
        if (!this.documents.loaded()) return 'Ready';
        const count = this.documents.documents().filter((d) => d.status === 'Indexed').length;
        return `Ready: ${count} ${count === 1 ? 'document' : 'documents'} searchable`;
      }
      case 'degraded':
        return 'AI service unavailable';
      case 'down':
        return 'API unreachable';
    }
  }
}
