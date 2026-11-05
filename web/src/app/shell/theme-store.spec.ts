import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { TestBed } from '@angular/core/testing';

import { THEME_KEY, ThemeStore } from './theme-store';

describe('ThemeStore', () => {
  const html = () => document.documentElement;

  function create(): ThemeStore {
    TestBed.resetTestingModule();
    return TestBed.inject(ThemeStore);
  }

  beforeEach(() => {
    localStorage.clear();
    html().removeAttribute('data-theme');
  });

  afterEach(() => {
    vi.restoreAllMocks();
    localStorage.clear();
    html().removeAttribute('data-theme');
  });

  it('follows the system when nothing has been chosen, and says nothing on the page', () => {
    const store = create();

    expect(store.choice()).toBe('system');
    expect(html().hasAttribute('data-theme')).toBe(false);
  });

  it('applies the choice remembered from last time', () => {
    localStorage.setItem(THEME_KEY, 'dark');

    const store = create();

    expect(store.choice()).toBe('dark');
    expect(html().getAttribute('data-theme')).toBe('dark');
  });

  it('ignores a remembered value it does not recognise instead of trusting it', () => {
    localStorage.setItem(THEME_KEY, 'purple');

    const store = create();

    expect(store.choice()).toBe('system');
    expect(html().hasAttribute('data-theme')).toBe(false);
  });

  it('applies and remembers a new choice', () => {
    const store = create();

    store.set('light');

    expect(store.choice()).toBe('light');
    expect(html().getAttribute('data-theme')).toBe('light');
    expect(localStorage.getItem(THEME_KEY)).toBe('light');
  });

  it('going back to the system setting clears the attribute and forgets the choice', () => {
    localStorage.setItem(THEME_KEY, 'dark');
    const store = create();

    store.set('system');

    expect(html().hasAttribute('data-theme')).toBe(false);
    expect(localStorage.getItem(THEME_KEY)).toBeNull();
  });

  it('still works for this visit when the browser will not let it read or write storage', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    const store = create();
    store.set('dark');
    expect(html().getAttribute('data-theme')).toBe('dark');
    expect(() => store.set('system')).not.toThrow();
    expect(html().hasAttribute('data-theme')).toBe(false);
  });

  it('uses the same storage key as the script that runs before the page paints, so the two can never disagree', () => {
    const script = readFileSync(resolve(process.cwd(), 'public', 'theme-init.js'), 'utf-8');

    expect(script).toContain(`'${THEME_KEY}'`);
  });

  it('the script that runs before the page paints applies only the two explicit themes', () => {
    const script = readFileSync(resolve(process.cwd(), 'public', 'theme-init.js'), 'utf-8');

    for (const [stored, expected] of [
      ['dark', 'dark'],
      ['light', 'light'],
      ['purple', null],
      [null, null],
    ] as const) {
      html().removeAttribute('data-theme');
      if (stored) localStorage.setItem(THEME_KEY, stored);
      else localStorage.removeItem(THEME_KEY);

      new Function(script)();

      expect(html().getAttribute('data-theme')).toBe(expected);
    }
  });
});
