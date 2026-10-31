import { DOCUMENT } from '@angular/common';
import { InjectionToken, inject } from '@angular/core';

/**
 * Leaves the app by loading a page from the server, instead of routing inside it. Used when the signed-in person changes (signing out, or
 * a session that has ended): the app's stores are singletons holding the previous person's documents and conversations, and a full load is
 * the one way to be sure none of it survives into the next session. A token so that tests can replace it.
 */
export const HARD_NAVIGATE = new InjectionToken<(url: string) => void>('HARD_NAVIGATE', {
  providedIn: 'root',
  factory: () => {
    const document = inject(DOCUMENT);
    return (url) => document.defaultView?.location.assign(url);
  },
});
