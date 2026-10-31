import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';

import { ApiClient } from '../api/api-client';
import type { ApiError } from '../api/api-error';
import { asApiError } from '../api/api-error';
import type { UserInfo } from '../api/models';
import { HARD_NAVIGATE } from './hard-navigate';

/**
 * Who is signed in. The session itself is an HttpOnly cookie the page cannot see, so the only way to know is to ask the server (GET
 * /api/auth/me); that same call is how the page receives its CSRF token. `user` is undefined until the first answer, null when nobody is
 * signed in.
 */
@Injectable({ providedIn: 'root' })
export class AuthStore {
  private readonly api = inject(ApiClient);
  private readonly hardNavigate = inject(HARD_NAVIGATE);
  private known: Promise<void> | null = null;

  readonly user = signal<UserInfo | null | undefined>(undefined);
  readonly signedIn = computed(() => !!this.user());
  readonly working = signal(false);
  readonly error = signal<ApiError | null>(null);

  /** Asks who is signed in, once. Every caller (each guard, the header) gets the same answer from the same request. */
  init(): Promise<void> {
    return (this.known ??= this.refresh());
  }

  /** Asks again. Needed after signing in or registering: the CSRF token is bound to who is signed in, and this call issues the new one. */
  async refresh(): Promise<void> {
    try {
      this.user.set(await firstValueFrom(this.api.me()));
    } catch {
      this.user.set(null); // not signed in, or the API cannot be reached: either way the sign-in page is the place to be
    }
  }

  login(email: string, password: string): Promise<boolean> {
    return this.enter(() => this.api.login({ email, password }));
  }

  register(email: string, password: string, inviteCode: string): Promise<boolean> {
    const code = inviteCode.trim();
    return this.enter(() => this.api.register({ email, password, ...(code === '' ? {} : { inviteCode: code }) }));
  }

  clearError(): void {
    this.error.set(null);
  }

  /** Ends the session, then loads the sign-in page afresh so nothing the previous person had on screen survives in memory. */
  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.api.logout());
    } catch {
      /* if the server could not be told, the page below is where the person finds out they are still signed in */
    } finally {
      this.hardNavigate('/login');
    }
  }

  private async enter(call: () => Observable<UserInfo>): Promise<boolean> {
    if (this.working()) return false;
    this.working.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(call());
      this.known = this.refresh();
      await this.known;
      return this.signedIn();
    } catch (error) {
      this.error.set(asApiError(error));
      return false;
    } finally {
      this.working.set(false);
    }
  }
}
