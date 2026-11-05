import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';

export type ThemeChoice = 'system' | 'light' | 'dark';

/** Where the choice is remembered. public/theme-init.js reads the same key before the page paints (a test keeps the two in step). */
export const THEME_KEY = 'notebook.theme';

/**
 * Light, dark, or whatever the device says. "System" is the default and is represented by the absence of a `data-theme` attribute, so the colours
 * follow the device's setting with no script involved (styles.css); an explicit choice sets the attribute, which wins over the device.
 * The choice is remembered in the browser. Storage can be blocked (private windows, strict settings), so every access is guarded and a
 * refusal only means the choice is not remembered next time.
 */
@Injectable({ providedIn: 'root' })
export class ThemeStore {
  private readonly root = inject(DOCUMENT).documentElement;

  readonly choice = signal<ThemeChoice>(this.remembered());

  constructor() {
    this.apply(this.choice());
  }

  set(choice: ThemeChoice): void {
    this.choice.set(choice);
    this.apply(choice);
    try {
      if (choice === 'system') localStorage.removeItem(THEME_KEY);
      else localStorage.setItem(THEME_KEY, choice);
    } catch {
      // not remembered next time; this visit is unaffected
    }
  }

  private apply(choice: ThemeChoice): void {
    if (choice === 'system') this.root.removeAttribute('data-theme');
    else this.root.setAttribute('data-theme', choice);
  }

  private remembered(): ThemeChoice {
    try {
      const stored = localStorage.getItem(THEME_KEY);
      return stored === 'light' || stored === 'dark' ? stored : 'system'; // anything else, including a value from some other version, is ignored
    } catch {
      return 'system';
    }
  }
}
