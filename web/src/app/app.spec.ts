import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { App } from './app';
import { AuthStore } from './auth/auth-store';
import { HealthBadge } from './shell/health-badge';

@Component({ selector: 'app-health-badge', template: '' })
class StubHealthBadge {}

describe('App header', () => {
  let fixture: ComponentFixture<App>;
  const user = signal<{ id: string; email: string } | null | undefined>(undefined);
  const logout = vi.fn();
  const init = vi.fn(() => Promise.resolve());

  const root = () => fixture.nativeElement as HTMLElement;
  const text = () => root().querySelector('header')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function show(who: { id: string; email: string } | null) {
    user.set(who);
    fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(() => {
    logout.mockClear();
    init.mockClear();
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), { provide: AuthStore, useValue: { user, init, logout } }],
    });
    TestBed.overrideComponent(App, { remove: { imports: [HealthBadge] }, add: { imports: [StubHealthBadge] } }); // the badge has its own tests
  });

  it('finds out who is signed in as soon as the app starts, on any page', async () => {
    await show(null);
    expect(init).toHaveBeenCalledTimes(1);
  });

  it('shows the navigation, who is signed in, and a way to sign out when someone is', async () => {
    await show({ id: '1', email: 'person@example.test' });

    expect(text()).toContain('Chat');
    expect(text()).toContain('Documents');
    expect(text()).toContain('person@example.test');
    expect(root().querySelector('nav a[href="/chat"]')).not.toBeNull();
    expect(root().querySelector('nav a[href="/documents"]')).not.toBeNull();
    expect(Array.from(root().querySelectorAll('button')).some((b) => b.textContent?.trim() === 'Sign out')).toBe(true);
  });

  it('offers the colour theme choice to everyone, signed in or not', async () => {
    await show(null);
    expect(root().querySelector('app-theme-toggle')).not.toBeNull();

    await show({ id: '1', email: 'person@example.test' });
    expect(root().querySelector('app-theme-toggle')).not.toBeNull();
  });

  it('signs out when the button is pressed', async () => {
    await show({ id: '1', email: 'person@example.test' });

    (Array.from(root().querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Sign out') as HTMLButtonElement).click();

    expect(logout).toHaveBeenCalledTimes(1);
  });

  it('shows no navigation, no name and no sign-out button when nobody is signed in', async () => {
    await show(null);

    expect(root().querySelector('nav')).toBeNull();
    expect(text()).not.toContain('Sign out');
    expect(text()).toContain('Notebook Assistant');
  });

  it('shows nothing person-specific while it is still finding out', async () => {
    await show(undefined as unknown as null);

    expect(root().querySelector('nav')).toBeNull();
    expect(text()).not.toContain('Sign out');
  });
});
