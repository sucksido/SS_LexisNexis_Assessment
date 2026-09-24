import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

import { OrderStatus } from '../core/models/order.models';

/** Renders an order status as a coloured pill. */
@Component({
  selector: 'app-status-pill',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="pill" [class]="'pill pill--' + status.toLowerCase()">{{ status }}</span>`,
})
export class StatusPillComponent {
  @Input({ required: true }) status!: OrderStatus;
}
