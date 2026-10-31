import { Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval } from 'rxjs';

/** Shown while the model works, which can take 5 to 45 seconds on the free tier: how long it has been, and a way out. */
@Component({
  selector: 'app-thinking-indicator',
  template: `
    <div class="thinking" role="status">
      <span class="dot" aria-hidden="true"></span>
      <span>Thinking… {{ seconds() }} s</span>
      @if (seconds() >= SLOW_AFTER_SECONDS) {
        <span class="muted">Free-tier models can be slow. Still working.</span>
      }
      <button type="button" class="button small" (click)="stop.emit()">Stop</button>
    </div>
  `,
  styles: `
    .thinking {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.5rem 0.75rem;
      padding: 0.5rem 0.9rem;
      color: var(--muted);
    }
    .dot {
      width: 0.6rem;
      height: 0.6rem;
      border-radius: 50%;
      background: var(--accent);
      animation: pulse 1.2s ease-in-out infinite;
    }
    @keyframes pulse {
      50% {
        opacity: 0.25;
      }
    }
    @media (prefers-reduced-motion: reduce) {
      .dot {
        animation: none;
      }
    }
  `,
})
export class ThinkingIndicator {
  protected readonly SLOW_AFTER_SECONDS = 15;

  /** When the question was sent (milliseconds since the epoch). */
  readonly since = input.required<number>();
  readonly stop = output<void>();

  private readonly now = signal(Date.now());
  protected readonly seconds = computed(() => Math.max(0, Math.floor((this.now() - this.since()) / 1000)));

  constructor() {
    interval(1000)
      .pipe(takeUntilDestroyed(inject(DestroyRef)))
      .subscribe(() => this.now.set(Date.now()));
  }
}
