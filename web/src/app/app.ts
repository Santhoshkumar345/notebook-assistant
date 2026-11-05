import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { AuthStore } from './auth/auth-store';
import { HealthBadge } from './shell/health-badge';
import { ThemeToggle } from './shell/theme-toggle';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, HealthBadge, ThemeToggle],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  protected readonly auth = inject(AuthStore);

  /** The first letter of the address, for the round avatar beside it. Decoration only: the address itself is what is shown and announced. */
  protected initial(email: string): string {
    return (email.trim()[0] ?? '?').toUpperCase();
  }

  constructor() {
    void this.auth.init(); // so the header knows who is signed in on any page, not only behind a guard
  }
}
