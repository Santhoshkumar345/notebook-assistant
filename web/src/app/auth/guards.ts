import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthStore } from './auth-store';

/** Protected pages: signed-out visitors are sent to sign in, and brought back to where they were going. The server enforces access too; this is the page being honest about it. */
export const authGuard: CanActivateFn = async (_route, state) => {
  // Everything is injected before the first await: after one, Angular's injection context is gone and inject() throws.
  const auth = inject(AuthStore);
  const router = inject(Router);
  await auth.init();
  return auth.signedIn() ? true : router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};

/** The sign-in and registration pages: someone already signed in has no business there. */
export const guestGuard: CanActivateFn = async () => {
  const auth = inject(AuthStore);
  const router = inject(Router);
  await auth.init();
  return auth.signedIn() ? router.createUrlTree(['/chat']) : true;
};
