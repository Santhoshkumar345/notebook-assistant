import { Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval } from 'rxjs';

/** Shown while the model works, which can take 5 to 45 seconds on the free tier: how long it has been, and a way out. */
@Component({
  selector: 'app-thinking-indicator',
  template: `
    <div class="thinking" role="status">
      <span class="dots" aria-hidden="true"><span class="dot"></span><span class="dot"></span><span class="dot"></span></span>
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
      align-self: flex-start;
      gap: 0.5rem 0.75rem;
      padding: 0.5rem 0.6rem 0.5rem 1rem;
      border: 1px solid var(--border);
      border-radius: 999px;
      background: var(--surface);
      box-shadow: var(--shadow-sm);
      color: var(--muted);
    }
    /* Three dots passing the baton: motion that says "working", with the words and the elapsed seconds beside it saying the same. */
    .dots {
      display: inline-flex;
      gap: 0.25rem;
    }
    .dot {
      width: 0.5rem;
      height: 0.5rem;
      border-radius: 50%;
      background: var(--brand-1);
      animation: pulse 1.2s ease-in-out infinite;
    }
    .dot:nth-child(2) {
      animation-delay: 0.15s;
    }
    .dot:nth-child(3) {
      animation-delay: 0.3s;
    }
    @keyframes pulse {
      50% {
        transform: translateY(-0.3rem);
        opacity: 0.4;
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
