import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';

import { AuthStore } from './auth-store';
import { authGuard, guestGuard } from './guards';

describe('route guards', () => {
  let router: Router;
  const signedIn = signal(false);
  let release: () => void;
  let init: Promise<void>;

  const route = {} as ActivatedRouteSnapshot;
  const at = (url: string) => ({ url }) as RouterStateSnapshot;
  const run = (guard: typeof authGuard, url = '/chat') => TestBed.runInInjectionContext(() => guard(route, at(url))) as Promise<boolean | UrlTree>;

  beforeEach(() => {
    signedIn.set(false);
    init = new Promise<void>((resolve) => (release = resolve));
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthStore, useValue: { init: () => init, signedIn } }],
    });
    router = TestBed.inject(Router);
  });

  describe('authGuard (pages that need an account)', () => {
    it('lets a signed-in person through', async () => {
      signedIn.set(true);
      release();
      expect(await run(authGuard)).toBe(true);
    });

    it('sends a signed-out visitor to sign in, remembering where they were going', async () => {
      release();
      const result = (await run(authGuard, '/chat/3acfb8d4-46b8-4816-845e-8d8f1b36e23d')) as UrlTree;

      expect(router.serializeUrl(result)).toBe('/login?returnUrl=%2Fchat%2F3acfb8d4-46b8-4816-845e-8d8f1b36e23d');
    });

    it('waits for the server to say who is signed in before deciding, instead of treating "not asked yet" as signed out', async () => {
      const pending = run(authGuard);
      let settled = false;
      void pending.then(() => (settled = true));

      await new Promise((resolve) => setTimeout(resolve));
      expect(settled).toBe(false); // still waiting for init

      signedIn.set(true); // the answer arrives: signed in
      release();
      expect(await pending).toBe(true);
    });
  });

  describe('guestGuard (the sign-in and registration pages)', () => {
    it('shows them to a signed-out visitor', async () => {
      release();
      expect(await run(guestGuard)).toBe(true);
    });

    it('sends someone who is already signed in to the app instead', async () => {
      signedIn.set(true);
      release();
      const result = (await run(guestGuard)) as UrlTree;

      expect(router.serializeUrl(result)).toBe('/chat');
    });
  });
});
