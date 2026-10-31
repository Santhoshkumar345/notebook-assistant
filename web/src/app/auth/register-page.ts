import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';

import { describeError } from '../api/api-error';
import { AuthStore } from './auth-store';
import { MAX_PASSWORD_CHARS, MIN_PASSWORD_CHARS, checkPassword, looksLikeEmail } from './credential-checks';

@Component({
  selector: 'app-register-page',
  imports: [RouterLink],
  templateUrl: './register-page.html',
  styleUrl: './auth-page.css',
})
export class RegisterPage {
  protected readonly auth = inject(AuthStore);
  private readonly router = inject(Router);
  protected readonly describeError = describeError;

  protected readonly min = MIN_PASSWORD_CHARS;
  protected readonly max = MAX_PASSWORD_CHARS;

  protected readonly email = signal('');
  protected readonly password = signal('');
  protected readonly inviteCode = signal('');

  /** Shown only once the person has typed something, so an empty form does not start out accusing them. */
  protected readonly passwordProblem = computed(() => (this.password() === '' ? null : checkPassword(this.password())));
  protected readonly canSubmit = computed(() => looksLikeEmail(this.email()) && checkPassword(this.password()) === null && !this.auth.working());

  constructor() {
    this.auth.clearError();
  }

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (!this.canSubmit()) return;

    if (await this.auth.register(this.email().trim(), this.password(), this.inviteCode())) {
      await this.router.navigateByUrl('/documents'); // a new account has nothing to ask about yet: start with a document
    } else {
      this.password.set('');
    }
  }

  protected set(field: 'email' | 'password' | 'inviteCode', event: Event): void {
    this[field].set((event.target as HTMLInputElement).value);
  }
}
