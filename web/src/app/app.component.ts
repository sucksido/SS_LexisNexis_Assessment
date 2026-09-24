import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="topbar">
      <div class="topbar__inner">
        <a class="topbar__brand" routerLink="/orders">Order Intake</a>
        <nav class="topbar__links">
          <a routerLink="/orders" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">
            Orders
          </a>
          <a routerLink="/orders/new" routerLinkActive="active">New order</a>
        </nav>
      </div>
    </header>

    <main class="shell">
      <router-outlet />
    </main>
  `,
})
export class AppComponent {}
