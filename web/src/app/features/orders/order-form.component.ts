import { CurrencyPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { ApiError } from '../../core/api/api-error';
import { OrderApiService } from '../../core/api/order-api.service';
import { CURRENCIES, SubmitOrderRequest } from '../../core/models/order.models';

/**
 * The submission form.
 *
 * Two things here are worth a second look.
 *
 * First, the running total is labelled a preview. The server computes the
 * authoritative figures — that is a requirement of the brief, and it is also
 * the only sane place for money arithmetic to live. Showing a number in the
 * browser that is not the number that gets stored would be a lie, so the label
 * says what it is.
 *
 * Second, validation is deliberately duplicated. Client-side rules exist to
 * give fast feedback; the server's rules exist because the client can be
 * bypassed. When the server rejects something the client let through, those
 * messages are bound back onto the offending controls rather than dumped in a
 * single banner, so the rep can see which row is wrong.
 */
@Component({
  selector: 'app-order-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, CurrencyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <div>
        <h1>New order</h1>
        <p class="muted small">
          The reference is issued by the system and cannot be edited. Sending the same one twice
          returns the original order instead of creating a second.
        </p>
      </div>
      <div class="page-head__actions">
        <button type="button" class="secondary" routerLink="/orders">Back to list</button>
      </div>
    </div>

    @if (submitError(); as err) {
      <div class="banner banner--error" role="alert">
        <strong>{{ err.problem.title ?? 'Could not submit the order' }}</strong>
        <div>{{ err.message }}</div>

        @if (err.existingOrderId; as existingId) {
          <div style="margin-top:.4rem">
            <a [routerLink]="['/orders', existingId]">Open the order that already uses it</a>
          </div>
        }
      </div>
    }

    @if (replayNotice(); as notice) {
      <div class="banner banner--info" role="status">{{ notice }}</div>
    }

    <form class="card" [formGroup]="form" (ngSubmit)="submit()" novalidate>
      <fieldset>
        <legend>Order</legend>

        <div class="grid-2">
          <div class="field">
            <label for="externalReference">Reference</label>
            <input
              id="externalReference"
              formControlName="externalReference"
              [placeholder]="referenceState() === 'loading' ? 'Generating…' : ''"
              [class.invalid]="referenceState() === 'failed'"
            />

            @switch (referenceState()) {
              @case ('loading') {
                <span class="muted small">Requesting the next reference…</span>
              }
              @case ('failed') {
                <span class="field-error">
                  Could not get a reference from the server.
                  <button type="button" class="link small" (click)="loadReference()">Try again</button>
                </span>
              }
              @default {
                <span class="muted small">Issued by the system — not editable.</span>
              }
            }
          </div>

          <div class="field">
            <label for="currency">Currency *</label>
            <select
              id="currency"
              formControlName="currency"
              [class.invalid]="isInvalid(form.controls.currency, 'currency')"
            >
              @for (code of currencies; track code) {
                <option [value]="code">{{ code }}</option>
              }
            </select>
            @if (isInvalid(form.controls.currency, 'currency')) {
              <span class="field-error">
                {{ messageFor(form.controls.currency, 'currency', 'Choose a currency.') }}
              </span>
            }
          </div>
        </div>

        <div class="field">
          <label for="notes">Notes</label>
          <textarea id="notes" formControlName="notes" rows="2"></textarea>
          <span class="muted small">
            Notes are not part of the duplicate check, so fixing a typo here and resending still
            counts as the same order.
          </span>
        </div>
      </fieldset>

      <fieldset formGroupName="customer">
        <legend>Customer</legend>

        <div class="grid-2">
          <div class="field">
            <label for="customerName">Name *</label>
            <input
              id="customerName"
              formControlName="name"
              [class.invalid]="isInvalid(customer.controls.name, 'customer.name')"
            />
            @if (isInvalid(customer.controls.name, 'customer.name')) {
              <span class="field-error">
                {{ messageFor(customer.controls.name, 'customer.name', 'A customer name is required.') }}
              </span>
            }
          </div>

          <div class="field">
            <label for="customerEmail">Email *</label>
            <input
              id="customerEmail"
              type="email"
              formControlName="email"
              [class.invalid]="isInvalid(customer.controls.email, 'customer.email')"
            />
            @if (isInvalid(customer.controls.email, 'customer.email')) {
              <span class="field-error">
                {{ messageFor(customer.controls.email, 'customer.email', 'Enter a valid email address.') }}
              </span>
            }
          </div>
        </div>
      </fieldset>

      <fieldset>
        <legend>Line items</legend>

        <table>
          <thead>
            <tr>
              <th scope="col">SKU *</th>
              <th scope="col">Description *</th>
              <th scope="col" class="numeric">Qty *</th>
              <th scope="col" class="numeric">Unit price *</th>
              <th scope="col" class="numeric">Line total</th>
              <th scope="col"></th>
            </tr>
          </thead>
          <tbody formArrayName="lines">
            @for (line of lines.controls; track line; let i = $index) {
              <tr [formGroupName]="i">
                <td>
                  <input
                    formControlName="sku"
                    placeholder="KB-001"
                    [attr.aria-label]="'SKU for line ' + (i + 1)"
                    [class.invalid]="isInvalid(line.controls.sku, 'lines[' + i + '].sku')"
                  />
                </td>
                <td>
                  <input
                    formControlName="name"
                    placeholder="Mechanical keyboard"
                    [attr.aria-label]="'Description for line ' + (i + 1)"
                    [class.invalid]="isInvalid(line.controls.name, 'lines[' + i + '].name')"
                  />
                </td>
                <td class="numeric">
                  <input
                    type="number"
                    min="1"
                    step="1"
                    style="width: 5.5rem"
                    formControlName="quantity"
                    [attr.aria-label]="'Quantity for line ' + (i + 1)"
                    [class.invalid]="isInvalid(line.controls.quantity, 'lines[' + i + '].quantity')"
                  />
                </td>
                <td class="numeric">
                  <input
                    type="number"
                    min="0"
                    step="0.01"
                    style="width: 7rem"
                    formControlName="unitPrice"
                    [attr.aria-label]="'Unit price for line ' + (i + 1)"
                    [class.invalid]="isInvalid(line.controls.unitPrice, 'lines[' + i + '].unitPrice')"
                  />
                </td>
                <td class="numeric">{{ lineTotal(i) | currency: currencyCode() }}</td>
                <td class="numeric">
                  <button
                    type="button"
                    class="link small"
                    [disabled]="lines.length === 1"
                    (click)="removeLine(i)"
                  >
                    Remove
                  </button>
                </td>
              </tr>

              @if (lineErrors(i); as errors) {
                <tr>
                  <td colspan="6" class="field-error small">{{ errors }}</td>
                </tr>
              }
            }
          </tbody>
        </table>

        <div class="button-row" style="margin-top:.75rem">
          <button type="button" class="secondary small" (click)="addLine()">Add line</button>
        </div>
      </fieldset>

      <div class="button-row">
        <button type="submit" [disabled]="submitting() || referenceState() !== 'ready'">
          {{ submitting() ? 'Submitting…' : 'Submit order' }}
        </button>
        <span class="muted small">
          Preview subtotal: <strong>{{ previewTotal() | currency: currencyCode() }}</strong> — the
          server recomputes and returns the authoritative figure.
        </span>
      </div>
    </form>
  `,
})
export class OrderFormComponent {
  private readonly fb = inject(FormBuilder).nonNullable;
  private readonly api = inject(OrderApiService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly submitting = signal(false);
  protected readonly submitError = signal<ApiError | null>(null);
  protected readonly replayNotice = signal<string | null>(null);

  protected readonly currencies = CURRENCIES;

  /**
   * Whether we have a reference to submit with.
   *
   * Tracked explicitly rather than inferred from the control being empty,
   * because "we have not asked yet", "we are asking" and "the server said no"
   * need three different things on screen and a blank box says none of them.
   */
  protected readonly referenceState = signal<'loading' | 'ready' | 'failed'>('loading');

  /** Field-path to messages, straight from the API's ProblemDetails `errors`. */
  private readonly serverErrors = signal<Record<string, string[]>>({});

  protected readonly form = this.fb.group({
    // Disabled, and the value comes from the server. Angular excludes disabled
    // controls from `form.value`, which is exactly the trap this would fall
    // into — `toRequest` uses `getRawValue()` for that reason.
    externalReference: [{ value: '', disabled: true }, [Validators.required, Validators.maxLength(64)]],
    // No pattern validator any more: a select cannot hold a malformed code, and
    // a client rule that can never fire is a rule that lies about being checked.
    currency: ['USD', [Validators.required]],
    notes: [''],
    customer: this.fb.group({
      name: ['', [Validators.required, Validators.maxLength(200)]],
      email: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
    }),
    lines: this.fb.array([this.newLine()]),
  });

  /**
   * A signal view of the form value, so the preview total recomputes without a
   * manual subscription. `toSignal` would be the tidier tool but pulls in a
   * second dependency on the form's lifetime; this is one line and obvious.
   */
  private readonly formValue = signal(this.form.getRawValue());

  protected readonly currencyCode = computed(() => {
    const code = this.formValue().currency?.toUpperCase() ?? '';
    // CurrencyPipe throws on a malformed code; fall back while the user types.
    return /^[A-Z]{3}$/.test(code) ? code : 'USD';
  });

  protected readonly previewTotal = computed(() =>
    this.formValue().lines.reduce(
      (running, line) => running + this.round(this.round(line.quantity * line.unitPrice)),
      0,
    ),
  );

  constructor() {
    this.form.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.formValue.set(this.form.getRawValue()));

    this.loadReference();
  }

  /**
   * Fetches the reference the server wants this order to carry.
   *
   * Called once on construction and again from the retry link. A failure here is
   * not cosmetic — without a reference there is nothing to submit — so it blocks
   * the submit buttons rather than letting the rep fill in the whole form and
   * discover the problem at the end.
   */
  protected loadReference(): void {
    this.referenceState.set('loading');

    this.api
      .nextReference()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (reference) => {
          this.form.controls.externalReference.setValue(reference);
          // A disabled control does not raise valueChanges, so the signal that
          // drives the preview has to be nudged by hand.
          this.formValue.set(this.form.getRawValue());
          this.referenceState.set('ready');
        },
        error: () => this.referenceState.set('failed'),
      });
  }

  // Inferred return types on purpose: writing them out means restating the
  // whole control tree, and the two copies drift the first time a field moves.
  protected get customer() {
    return this.form.controls.customer;
  }

  protected get lines() {
    return this.form.controls.lines;
  }

  protected addLine(): void {
    this.lines.push(this.newLine());
  }

  protected removeLine(index: number): void {
    // Never let the form reach zero lines: the server rejects that, and an
    // empty table is a confusing thing to hand back to someone.
    if (this.lines.length > 1) {
      this.lines.removeAt(index);
    }
  }

  protected lineTotal(index: number): number {
    const line = this.formValue().lines[index];
    return line ? this.round(line.quantity * line.unitPrice) : 0;
  }

  /** Any server-side message for a line that did not map onto one control. */
  protected lineErrors(index: number): string | null {
    const prefix = `lines[${index}]`;
    const messages = Object.entries(this.serverErrors())
      .filter(([key]) => key === prefix || key.startsWith(`${prefix}.`))
      .flatMap(([, value]) => value);

    return messages.length ? messages.join(' ') : null;
  }

  protected isInvalid(control: AbstractControl, path: string): boolean {
    return (control.invalid && (control.touched || control.dirty)) || path in this.serverErrors();
  }

  protected messageFor(control: AbstractControl, path: string, fallback: string): string {
    const fromServer = this.serverErrors()[path];
    if (fromServer?.length) {
      return fromServer.join(' ');
    }

    return control.hasError('email') ? 'Enter a valid email address.' : fallback;
  }

  protected submit(): void {
    this.serverErrors.set({});
    this.replayNotice.set(null);
    this.submitError.set(null);

    if (this.form.invalid) {
      // Untouched controls show no error, so mark everything before complaining.
      this.form.markAllAsTouched();
      return;
    }

    this.send();
  }

  private send(): void {
    // Belt and braces. The buttons are disabled without a reference, but
    // `form.invalid` cannot catch a missing one — Angular skips disabled
    // controls when it validates — so the check is made here explicitly rather
    // than relying on the template to be the only thing standing in the way.
    if (this.referenceState() !== 'ready') {
      return;
    }

    this.submitting.set(true);

    this.api.submit(this.toRequest()).subscribe({
      next: (result) => {
        this.submitting.set(false);

        if (result.isReplay) {
          this.replayNotice.set(
            `This order had already been recorded under ${result.order.externalReference}. ` +
              'Showing the original rather than creating a duplicate.',
          );
        }

        void this.router.navigate(['/orders', result.order.id], {
          state: { replay: result.isReplay },
        });
      },
      error: (error: ApiError) => {
        this.submitting.set(false);
        this.submitError.set(error);
        this.serverErrors.set(error.fieldErrors);
      },
    });
  }

  private toRequest(): SubmitOrderRequest {
    const value = this.form.getRawValue();

    return {
      externalReference: value.externalReference.trim(),
      currency: value.currency.trim().toUpperCase(),
      notes: value.notes.trim() ? value.notes.trim() : null,
      customer: {
        name: value.customer.name.trim(),
        email: value.customer.email.trim(),
      },
      lines: value.lines.map((line) => ({
        sku: line.sku.trim(),
        name: line.name.trim(),
        quantity: Number(line.quantity),
        unitPrice: Number(line.unitPrice),
      })),
    };
  }

  private newLine() {
    return this.fb.group({
      sku: ['', [Validators.required, Validators.maxLength(64)]],
      name: ['', [Validators.required, Validators.maxLength(200)]],
      quantity: [1, [Validators.required, Validators.min(1)]],
      unitPrice: [0, [Validators.required, Validators.min(0)]],
    });
  }

  /** Preview-only rounding. The stored figures come from the server. */
  private round(value: number): number {
    return Math.round((Number(value) || 0) * 100) / 100;
  }
}
