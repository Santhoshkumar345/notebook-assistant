import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { HealthBadge } from './shell/health-badge';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, HealthBadge],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {}
