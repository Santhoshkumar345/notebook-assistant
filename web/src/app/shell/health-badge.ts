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
      <span>{{ label().headline }}<span class="detail">{{ label().detail }}</span></span>
    </span>
  `,
  styles: `
    .health {
      display: inline-flex;
      align-items: center;
      gap: 0.45rem;
      padding: 0.2rem 0.7rem 0.2rem 0.6rem;
      border: 1px solid var(--border);
      border-radius: 999px;
      background: var(--surface-2);
      font-size: 0.8125rem;
      color: var(--muted);
    }
    @media (max-width: 29.99rem) {
      .detail {
        position: absolute;
        width: 1px;
        height: 1px;
        overflow: hidden;
        clip: rect(0, 0, 0, 0);
        white-space: nowrap;
      }
    }
    .dot {
      width: 0.6rem;
      height: 0.6rem;
      border-radius: 50%;
      background: var(--muted);
    }
    .ok .dot {
      background: var(--ok-fg);
      box-shadow: 0 0 0 3px var(--ok-bg);
    }
    .degraded .dot {
      background: var(--warn-fg);
      box-shadow: 0 0 0 3px var(--warn-bg);
    }
    .down .dot {
      background: var(--bad-fg);
      box-shadow: 0 0 0 3px var(--bad-bg);
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

  /** The headline is what matters at a glance; the detail follows it and is only visually dropped on a phone (still read out), to keep the top bar short. */
  protected label(): { headline: string; detail: string } {
    const health = this.health();
    switch (health.state) {
      case 'checking':
        return { headline: 'Checking…', detail: '' };
      case 'ok': {
        // The document count is the signed-in person's own (from their documents), never a figure for the whole system.
        if (!this.documents.loaded()) return { headline: 'Ready', detail: '' };
        const count = this.documents.documents().filter((d) => d.status === 'Indexed').length;
        return { headline: 'Ready', detail: `: ${count} ${count === 1 ? 'document' : 'documents'} searchable` };
      }
      case 'degraded':
        return { headline: 'AI service unavailable', detail: '' };
      case 'down':
        return { headline: 'API unreachable', detail: '' };
    }
  }
}
