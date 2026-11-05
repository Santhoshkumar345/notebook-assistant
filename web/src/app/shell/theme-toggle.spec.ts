import { ComponentFixture, TestBed } from '@angular/core/testing';

import { THEME_KEY } from './theme-store';
import { ThemeToggle } from './theme-toggle';

describe('ThemeToggle', () => {
  let fixture: ComponentFixture<ThemeToggle>;
  const root = () => fixture.nativeElement as HTMLElement;
  const radios = () => Array.from(root().querySelectorAll<HTMLInputElement>('input[type=radio]'));
  const radio = (value: string) => radios().find((r) => r.value === value)!;

  beforeEach(async () => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    TestBed.configureTestingModule({ imports: [ThemeToggle] });
    fixture = TestBed.createComponent(ThemeToggle);
    await fixture.whenStable();
  });

  afterEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
  });

  it('offers system, light and dark as one group of radio buttons with a name', () => {
    expect(radios().map((r) => r.value)).toEqual(['system', 'light', 'dark']);
    expect(new Set(radios().map((r) => r.name)).size).toBe(1);
    expect(root().querySelector('legend')?.textContent?.trim()).toBe('Colour theme');
  });

  it('gives every option a name a person using a screen reader can hear, not just an icon', () => {
    for (const r of radios()) {
      const label = r.closest('label');
      expect(label?.textContent?.trim().length ?? 0, r.value).toBeGreaterThan(0);
    }
    expect(radios().map((r) => r.closest('label')?.textContent?.trim())).toEqual(['System', 'Light', 'Dark']);
  });

  it('starts on the system option', () => {
    expect(radio('system').checked).toBe(true);
    expect(radio('dark').checked).toBe(false);
  });

  it('choosing dark switches the page and remembers it', async () => {
    radio('dark').click();
    await fixture.whenStable();

    expect(radio('dark').checked).toBe(true);
    expect(radio('system').checked).toBe(false);
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem(THEME_KEY)).toBe('dark');
  });

  it('choosing light after dark switches back', async () => {
    radio('dark').click();
    radio('light').click();
    await fixture.whenStable();

    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(radio('light').checked).toBe(true);
  });

  it('choosing system returns to following the device', async () => {
    radio('dark').click();
    radio('system').click();
    await fixture.whenStable();

    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });
});
