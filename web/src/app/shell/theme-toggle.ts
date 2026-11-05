import { Component, inject } from '@angular/core';

import { ThemeChoice, ThemeStore } from './theme-store';

/** Three radio buttons drawn as a small segmented switch: follow the device, light, dark. Native radios, so the arrow keys and screen readers just work. */
@Component({
  selector: 'app-theme-toggle',
  template: `
    <fieldset class="theme">
      <legend class="sr-only">Colour theme</legend>
      @for (option of options; track option.value) {
        <label [title]="option.hint">
          <input type="radio" name="theme" [value]="option.value" [checked]="theme.choice() === option.value" (change)="theme.set(option.value)" />
          <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
            @switch (option.value) {
              @case ('system') {
                <rect x="3" y="4" width="14" height="9.5" rx="1.6" />
                <path d="M7 17h6M10 13.5V17" />
              }
              @case ('light') {
                <circle cx="10" cy="10" r="3.2" />
                <path d="M10 2.5v1.8M10 15.7v1.8M2.5 10h1.8M15.7 10h1.8M4.7 4.7l1.3 1.3M14 14l1.3 1.3M4.7 15.3 6 14M14 6l1.3-1.3" />
              }
              @case ('dark') {
                <path d="M16.5 11.5A6.5 6.5 0 0 1 8.5 3.5a6.5 6.5 0 1 0 8 8z" />
              }
            }
          </svg>
          <span class="sr-only">{{ option.label }}</span>
        </label>
      }
    </fieldset>
  `,
  styles: `
    .theme {
      display: inline-flex;
      gap: 0.15rem;
      margin: 0;
      padding: 0.15rem;
      border: 1px solid var(--border);
      border-radius: 999px;
      background: var(--surface-2);
    }
    label {
      position: relative;
      display: grid;
      place-items: center;
      width: 1.9rem;
      height: 1.9rem;
      border-radius: 50%;
      color: var(--muted);
      cursor: pointer;
    }
    label:hover {
      color: var(--text);
    }
    input {
      position: absolute;
      inset: 0;
      width: 100%;
      height: 100%;
      margin: 0;
      opacity: 0;
      cursor: pointer;
    }
    svg {
      width: 1.05rem;
      height: 1.05rem;
      fill: none;
      stroke: currentColor;
      stroke-width: 1.7;
      stroke-linecap: round;
      stroke-linejoin: round;
      pointer-events: none;
    }
    label:has(input:checked) {
      background: var(--accent-soft);
      color: var(--accent);
      box-shadow: var(--shadow-sm);
    }
    label:has(input:focus-visible) {
      outline: 2px solid var(--focus);
      outline-offset: 2px;
    }
  `,
})
export class ThemeToggle {
  protected readonly theme = inject(ThemeStore);
  protected readonly options: { value: ThemeChoice; label: string; hint: string }[] = [
    { value: 'system', label: 'System', hint: 'Match this device' },
    { value: 'light', label: 'Light', hint: 'Light theme' },
    { value: 'dark', label: 'Dark', hint: 'Dark theme' },
  ];
}
