import { CurrencyPipe, DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, OnInit, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';

import { ApiError } from '../../core/api/api-error';
import { OrderApiService } from '../../core/api/order-api.service';
import { OrderResponse, OrderStatus } from '../../core/models/order.models';
import { StatusPillComponent } from '../../shared/status-pill.component';

/**
 * A single order: line items, server-computed totals, and the status controls.
 *
 * The buttons come from `order.allowedTransitions`, which the API sends on
 * every order response. The client therefore has no copy of the transition
 * rules at all — it cannot offer a move the server would reject, and when the
 * rules change the UI follows without a deployment.
 *
 * The rejection path is still handled, because between rendering the page and
 * clicking the button someone else may have moved the order on. That 409 comes
 * back carrying the transitions that *are* legal now, so the screen corrects
 * itself instead of just apologising.
 */
@Component({
  selector: 'app-order-detail',
  standalone: true,
  imports: [RouterLink, CurrencyPipe, DatePipe, StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <div>
        <h1>{{ order()?.externalReference ?? 'Order' }}</h1>
        <p class="muted small">Totals and status are computed and enforced by the server.</p>
      </div>
      <div class="page-head__actions">
        <button type="button" class="secondary" routerLink="/orders">Back to list</button>
      </div>
    </div>

    @if (replayNotice()) {
      <div class="banner banner--info" role="status">
        This submission matched an order that already existed, so you are looking at the original.
        No duplicate was created.
      </div>
    }

    @if (statusMessage(); as message) {
      <div class="banner banner--ok" role="status">{{ message }}</div>
    }

    @if (statusError(); as err) {
      <div class="banner banner--error" role="alert">
        <strong>{{ err.problem.title ?? 'That did not work' }}</strong>
        <div>{{ err.message }}</div>
      </div>
    }

    @if (loadError(); as err) {
      <div class="banner banner--error" role="alert">
        <strong>Could not load this order.</strong> {{ err.message }}
      </div>
    }

    @if (order(); as current) {
      <div class="card">
        <dl class="detail">
          <dt>Status</dt>
          <dd><app-status-pill [status]="current.status" /></dd>

          <dt>Customer</dt>
          <dd>{{ current.customer.name }} &middot; {{ current.customer.email }}</dd>

          <dt>Reference</dt>
          <dd>{{ current.externalReference }}</dd>

          <dt>Created</dt>
          <dd>{{ current.createdAtUtc | date: 'medium' }}</dd>

          <dt>Last updated</dt>
          <dd>{{ current.updatedAtUtc | date: 'medium' }}</dd>

          @if (current.notes) {
            <dt>Notes</dt>
            <dd>{{ current.notes }}</dd>
          }
        </dl>
      </div>

      <div class="card">
        <h2>Line items</h2>
        <table>
          <thead>
            <tr>
              <th scope="col">SKU</th>
              <th scope="col">Description</th>
              <th scope="col" class="numeric">Qty</th>
              <th scope="col" class="numeric">Unit price</th>
              <th scope="col" class="numeric">Line total</th>
            </tr>
          </thead>
          <tbody>
            @for (line of current.lines; track line.id) {
              <tr>
                <td>{{ line.sku }}</td>
                <td>{{ line.name }}</td>
                <td class="numeric">{{ line.quantity }}</td>
                <td class="numeric">{{ line.unitPrice | currency: current.currency }}</td>
                <td class="numeric">{{ line.lineTotal | currency: current.currency }}</td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr>
              <td colspan="4" class="numeric"><strong>Subtotal</strong></td>
              <td class="numeric">{{ current.subtotal | currency: current.currency }}</td>
            </tr>
            <tr>
              <td colspan="4" class="numeric"><strong>Total</strong></td>
              <td class="numeric">
                <strong>{{ current.total | currency: current.currency }}</strong>
              </td>
            </tr>
          </tfoot>
        </table>
      </div>

      <div class="card">
        <h2>Status</h2>

        @if (current.allowedTransitions.length === 0) {
          <p class="muted">
            <strong>{{ current.status }}</strong> is a final state, so there is nothing further to
            do. The server would refuse any change, which is why no buttons are offered.
          </p>
        } @else {
          <p class="muted small">
            These buttons are built from the transitions the server sent with this order, not from
            rules copied into the browser.
          </p>
          <div class="button-row">
            @for (next of current.allowedTransitions; track next) {
              <button
                type="button"
                [class.danger]="next === 'Cancelled'"
                [class.secondary]="next !== 'Cancelled'"
                [disabled]="changing()"
                (click)="changeStatus(next)"
              >
                {{ verbFor(next) }}
              </button>
            }
            @if (changing()) {
              <span class="spinner">Updating…</span>
            }
          </div>
        }
      </div>

      <div class="card">
        <h2>Send this order again</h2>
        <p class="muted small">
          Posts this exact order back to <code>POST /api/orders</code> — same reference, same
          customer, same lines. It is what a rep's browser does when it retries a request whose
          response was lost. Nothing new should appear in the list.
        </p>

        @if (resendOutcome(); as outcome) {
          <div
            class="banner"
            [class.banner--ok]="outcome.isReplay && outcome.sameOrder"
            [class.banner--error]="!outcome.isReplay || !outcome.sameOrder"
            role="status"
          >
            @if (outcome.isReplay && outcome.sameOrder) {
              The server recognised it and returned this same order
              ({{ current.externalReference }}) with <code>200 OK</code> and
              <code>X-Idempotent-Replay: true</code>. No second order was written.
            } @else {
              Unexpected: the server treated that as a new order. That is the bug this whole
              feature exists to prevent, so it is worth reporting.
            }
          </div>
        }

        @if (resendError(); as err) {
          <div class="banner banner--error" role="alert">
            <strong>{{ err.problem.title ?? 'That did not work' }}</strong>
            <div>{{ err.message }}</div>
          </div>
        }

        <div class="button-row">
          <button type="button" class="secondary" [disabled]="resending()" (click)="resend()">
            {{ resending() ? 'Sending…' : 'Send again (same payload)' }}
          </button>
        </div>
      </div>
    } @else if (!loadError()) {
      <div class="card"><p class="empty">Loading…</p></div>
    }
  `,
})
export class OrderDetailComponent implements OnInit {
  /** Bound straight from the `:orderId` route parameter. */
  @Input({ required: true }) orderId!: string;

  protected readonly order = signal<OrderResponse | null>(null);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly statusError = signal<ApiError | null>(null);
  protected readonly statusMessage = signal<string | null>(null);
  protected readonly changing = signal(false);
  protected readonly replayNotice = signal(false);

  protected readonly resending = signal(false);
  protected readonly resendError = signal<ApiError | null>(null);
  protected readonly resendOutcome = signal<{ isReplay: boolean; sameOrder: boolean } | null>(null);

  private readonly api = inject(OrderApiService);
  private readonly router = inject(Router);

  ngOnInit(): void {
    // The submission form navigates here with `state: { replay }` so the notice
    // survives the redirect without becoming a query parameter that would stay
    // in the URL and reappear on a refresh.
    const state = (this.router.getCurrentNavigation()?.extras.state ?? history.state) as
      | { replay?: boolean }
      | undefined;

    this.replayNotice.set(state?.replay === true);

    this.load();
  }

  protected changeStatus(next: OrderStatus): void {
    this.changing.set(true);
    this.statusError.set(null);
    this.statusMessage.set(null);

    this.api.changeStatus(this.orderId, next).subscribe({
      next: (result) => {
        this.changing.set(false);
        this.order.set(result.order);

        this.statusMessage.set(
          result.changed
            ? `Order is now ${result.order.status}.`
            : `Order was already ${result.order.status}, so nothing changed.`,
        );
      },
      error: (error: ApiError) => {
        this.changing.set(false);
        this.statusError.set(error);

        // The 409 tells us what is legal now. Trusting it over our stale copy
        // means the buttons repaint correctly rather than inviting the same
        // rejected click again.
        const current = this.order();
        if (current && error.allowedTransitions.length > 0) {
          this.order.set({ ...current, allowedTransitions: error.allowedTransitions });
        }

        // Re-read the order so the rest of the screen reflects reality too.
        this.load({ quiet: true });
      },
    });
  }

  /**
   * Resends this order exactly as it stands.
   *
   * The payload is rebuilt from the stored order rather than remembered from
   * the form, and that is the point: currency, SKUs, quantities and unit prices
   * are the fields the fingerprint is taken over, so a reconstruction that
   * matches on those *is* the same request as far as the server is concerned.
   *
   * This lives here rather than on the form because the form no longer has a
   * way to repeat itself — it is issued a fresh reference every time it opens,
   * so a second visit is a genuinely new order. The detail page is the one
   * place that still knows a reference that has already been used.
   *
   * The outcome is reported rather than assumed. A button that always printed
   * "no duplicate created" would be decoration; this one checks that the server
   * really did return *this* order, and says so loudly if it did not.
   */
  protected resend(): void {
    const current = this.order();

    if (!current) {
      return;
    }

    this.resending.set(true);
    this.resendError.set(null);
    this.resendOutcome.set(null);

    this.api
      .submit({
        externalReference: current.externalReference,
        customer: { email: current.customer.email, name: current.customer.name },
        currency: current.currency,
        notes: current.notes ?? null,
        lines: current.lines.map((line) => ({
          sku: line.sku,
          name: line.name,
          quantity: line.quantity,
          unitPrice: line.unitPrice,
        })),
      })
      .subscribe({
        next: (result) => {
          this.resending.set(false);
          this.resendOutcome.set({
            isReplay: result.isReplay,
            sameOrder: result.order.id === current.id,
          });
        },
        error: (error: ApiError) => {
          this.resending.set(false);
          this.resendError.set(error);
        },
      });
  }

  protected verbFor(status: OrderStatus): string {
    switch (status) {
      case 'Confirmed':
        return 'Confirm';
      case 'Fulfilled':
        return 'Mark fulfilled';
      case 'Cancelled':
        return 'Cancel order';
      default:
        return status;
    }
  }

  private load(options: { quiet?: boolean } = {}): void {
    this.api.get(this.orderId).subscribe({
      next: (order) => {
        this.order.set(order);
        this.loadError.set(null);
      },
      error: (error: ApiError) => {
        // A refresh triggered by a failed status change should not replace the
        // message explaining that failure with a second, vaguer one.
        if (!options.quiet) {
          this.loadError.set(error);
        }
      },
    });
  }
}
