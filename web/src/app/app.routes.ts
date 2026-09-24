import { Routes } from '@angular/router';

/**
 * Every feature is lazily loaded. The bundle the browser downloads to show the
 * order list does not contain the submission form, and vice versa.
 */
export const routes: Routes = [
  {
    path: 'orders',
    title: 'Orders',
    loadComponent: () =>
      import('./features/orders/order-list.component').then((m) => m.OrderListComponent),
  },
  {
    path: 'orders/new',
    title: 'New order',
    loadComponent: () =>
      import('./features/orders/order-form.component').then((m) => m.OrderFormComponent),
  },
  {
    // withComponentInputBinding() in app.config.ts binds this parameter
    // straight into the component's `orderId` input, so the component never
    // has to touch ActivatedRoute.
    path: 'orders/:orderId',
    title: 'Order detail',
    loadComponent: () =>
      import('./features/orders/order-detail.component').then((m) => m.OrderDetailComponent),
  },
  { path: '', pathMatch: 'full', redirectTo: 'orders' },
  { path: '**', redirectTo: 'orders' },
];
