import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { AuthStore } from './auth/auth-store';
import { HealthBadge } from './shell/health-badge';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, HealthBadge],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  protected readonly auth = inject(AuthStore);

  constructor() {
    void this.auth.init(); // so the header knows who is signed in on any page, not only behind a guard
  }
}
