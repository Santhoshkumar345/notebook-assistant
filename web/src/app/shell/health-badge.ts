import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { merge, skip, switchMap, timer } from 'rxjs';

import { ApiClient } from '../api/api-client';
import type { Health } from '../api/models';
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

  protected readonly health = signal<Health>({ state: 'checking' });

  constructor() {
    // Every 30 seconds, and straight away after a document is added, replaced, re-indexed, deleted or synced.
    const afterWrite = toObservable(this.documents.writes).pipe(skip(1));
    merge(timer(0, HEALTH_POLL_MS), afterWrite)
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
      case 'ok':
        return `Ready: ${health.documents} ${health.documents === 1 ? 'document' : 'documents'} searchable`;
      case 'degraded':
        return 'AI service unavailable';
      case 'down':
        return 'API unreachable';
    }
  }
}
