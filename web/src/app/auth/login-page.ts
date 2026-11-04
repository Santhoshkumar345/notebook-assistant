import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { describeError } from '../api/api-error';
import { AuthShowcase } from './auth-showcase';
import { AuthStore } from './auth-store';
import { safeReturnUrl } from './safe-return-url';

@Component({
  selector: 'app-login-page',
  imports: [RouterLink, AuthShowcase],
  templateUrl: './login-page.html',
  styleUrl: './auth-page.css',
})
export class LoginPage {
  protected readonly auth = inject(AuthStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  protected readonly describeError = describeError;

  protected readonly email = signal('');
  protected readonly password = signal('');
  /** The session ended while the person was using the app: they are told why they are here. */
  protected readonly expired = this.route.snapshot.queryParamMap.get('expired') === '1';
  protected readonly canSubmit = computed(() => this.email().trim() !== '' && this.password() !== '' && !this.auth.working());

  constructor() {
    this.auth.clearError(); // an error from the registration page should not greet someone here
  }

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (!this.canSubmit()) return;

    if (await this.auth.login(this.email().trim(), this.password())) {
      await this.router.navigateByUrl(safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')));
    } else {
      this.password.set(''); // a failed attempt is never left sitting in the box
    }
  }

  protected set(field: 'email' | 'password', event: Event): void {
    (field === 'email' ? this.email : this.password).set((event.target as HTMLInputElement).value);
  }
}
