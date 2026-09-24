import { CurrencyPipe, DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { debounceTime, distinctUntilChanged } from 'rxjs';

import { ApiError } from '../../core/api/api-error';
import { OrderApiService } from '../../core/api/order-api.service';
import {
  ORDER_STATUSES,
  OrderStatus,
  OrderSummary,
  PagedResult,
} from '../../core/models/order.models';
import { StatusPillComponent } from '../../shared/status-pill.component';

/**
 * The order list: newest first, with a status filter and a free-text search.
 *
 * Both filters are sent to the server rather than applied to the rows already
 * in the browser. Client-side filtering is less code and is correct only while
 * the whole result set fits on one page — after that it quietly starts hiding
 * matches that live on page two, which is the worst kind of bug because the
 * screen still looks right.
 */
@Component({
  selector: 'app-order-list',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, CurrencyPipe, DatePipe, StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <div>
        <h1>Orders</h1>
        <p class="muted small">Most recent first.</p>
      </div>
      <div class="page-head__actions">
        <button type="button" routerLink="/orders/new">New order</button>
      </div>
    </div>

    @if (error(); as err) {
      <div class="banner banner--error" role="alert">
        <strong>Could not load orders.</strong> {{ err.message }}
      </div>
    }

    <div class="card">
      <div class="toolbar">
        <div class="field">
          <label for="search">Search</label>
          <input
            id="search"
            type="search"
            placeholder="Reference, customer name or email"
            [formControl]="search"
          />
        </div>

        <div class="field">
          <label for="status">Status</label>
          <select id="status" [formControl]="status">
            <option [ngValue]="null">All statuses</option>
            @for (option of statuses; track option) {
              <option [ngValue]="option">{{ option }}</option>
            }
          </select>
        </div>

        <button type="button" class="secondary" (click)="reset()">Clear</button>

        @if (loading()) {
          <span class="spinner">Loading…</span>
        }
      </div>
    </div>

    <div class="card">
      @if (result(); as page) {
        @if (page.items.length === 0) {
          <p class="empty">
            No orders match. <a routerLink="/orders/new">Submit one</a> to get started.
          </p>
        } @else {
          <table>
            <thead>
              <tr>
                <th scope="col">Reference</th>
                <th scope="col">Customer</th>
                <th scope="col">Status</th>
                <th scope="col" class="numeric">Lines</th>
                <th scope="col" class="numeric">Total</th>
                <th scope="col">Created</th>
              </tr>
            </thead>
            <tbody>
              @for (order of page.items; track order.id) {
                <tr>
                  <td>
                    <a [routerLink]="['/orders', order.id]">{{ order.externalReference }}</a>
                  </td>
                  <td>
                    {{ order.customerName }}
                    <div class="muted small">{{ order.customerEmail }}</div>
                  </td>
                  <td><app-status-pill [status]="order.status" /></td>
                  <td class="numeric">{{ order.lineCount }}</td>
                  <td class="numeric">{{ order.total | currency: order.currency }}</td>
                  <td class="small">{{ order.createdAtUtc | date: 'medium' }}</td>
                </tr>
              }
            </tbody>
          </table>

          <div class="pager">
            <button
              type="button"
              class="secondary small"
              [disabled]="page.page <= 1 || loading()"
              (click)="goTo(page.page - 1)"
            >
              Previous
            </button>
            <span class="muted small">
              Page {{ page.page }} of {{ page.totalPages || 1 }} · {{ page.totalCount }} order(s)
            </span>
            <button
              type="button"
              class="secondary small"
              [disabled]="!page.hasNextPage || loading()"
              (click)="goTo(page.page + 1)"
            >
              Next
            </button>
          </div>
        }
      } @else if (!error()) {
        <p class="empty">Loading orders…</p>
      }
    </div>
  `,
})
export class OrderListComponent implements OnInit {
  protected readonly statuses = ORDER_STATUSES;

  protected readonly search = new FormControl<string>('', { nonNullable: true });
  protected readonly status = new FormControl<OrderStatus | null>(null);

  protected readonly result = signal<PagedResult<OrderSummary> | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  private page = 1;
  private readonly pageSize = 20;

  private readonly api = inject(OrderApiService);
  private readonly destroyRef = inject(DestroyRef);

  ngOnInit(): void {
    // Typing is not a page request. Waiting for a pause keeps one search from
    // firing a call per keystroke, and distinctUntilChanged drops the ones that
    // did not actually change the term (arrow keys, re-typing the same letter).
    this.search.valueChanges
      .pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.goTo(1));

    // The status dropdown is a deliberate choice, so it takes effect at once.
    this.status.valueChanges
      .pipe(distinctUntilChanged(), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.goTo(1));

    this.load();
  }

  protected goTo(page: number): void {
    this.page = Math.max(1, page);
    this.load();
  }

  protected reset(): void {
    this.search.setValue('', { emitEvent: false });
    this.status.setValue(null, { emitEvent: false });
    this.goTo(1);
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api
      .list({
        page: this.page,
        pageSize: this.pageSize,
        status: this.status.value,
        search: this.search.value,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (page) => {
          this.result.set(page);
          this.loading.set(false);
        },
        error: (err: ApiError) => {
          this.error.set(err);
          this.loading.set(false);
        },
      });
  }
}
