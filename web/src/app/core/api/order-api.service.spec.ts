import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { ApiError } from './api-error';
import { OrderApiService } from './order-api.service';
import { OrderResponse } from '../models/order.models';

/**
 * The service is where the wire format meets the app, so this is where the
 * behaviour that the rest of the code depends on gets pinned down: that a
 * replay is reported as a replay, that filters are only sent when set, and that
 * failures arrive as ApiError rather than as raw HTTP.
 */
describe('OrderApiService', () => {
  let service: OrderApiService;
  let http: HttpTestingController;

  const anOrder = (): OrderResponse => ({
    id: '11111111-1111-1111-1111-111111111111',
    externalReference: 'PO-1001',
    customer: { id: '22222222-2222-2222-2222-222222222222', email: 'ada@contoso.com', name: 'Ada' },
    status: 'Pending',
    currency: 'USD',
    notes: null,
    subtotal: 100,
    total: 100,
    createdAtUtc: '2026-09-21T09:00:00+00:00',
    updatedAtUtc: '2026-09-21T09:00:00+00:00',
    lines: [],
    allowedTransitions: ['Confirmed', 'Cancelled'],
  });

  const aRequest = () => ({
    externalReference: 'PO-1001',
    currency: 'USD',
    notes: null,
    customer: { name: 'Ada', email: 'ada@contoso.com' },
    lines: [{ sku: 'KB-001', name: 'Keyboard', quantity: 1, unitPrice: 100 }],
  });

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(OrderApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reports a 201 as a new order', () => {
    let replayed: boolean | undefined;

    service.submit(aRequest()).subscribe((result) => (replayed = result.isReplay));

    http
      .expectOne('/api/orders')
      .flush(anOrder(), { status: 201, statusText: 'Created', headers: { 'X-Idempotent-Replay': 'false' } });

    expect(replayed).toBe(false);
  });

  it('reports a 200 with the replay header as a replay', () => {
    let replayed: boolean | undefined;

    service.submit(aRequest()).subscribe((result) => (replayed = result.isReplay));

    http
      .expectOne('/api/orders')
      .flush(anOrder(), { status: 200, statusText: 'OK', headers: { 'X-Idempotent-Replay': 'true' } });

    expect(replayed).toBe(true);
  });

  it('omits filter parameters that are not set', () => {
    service.list({ page: 2 }).subscribe();

    const request = http.expectOne((candidate) => candidate.url === '/api/orders');

    expect(request.request.params.get('page')).toBe('2');
    expect(request.request.params.has('status')).toBe(false);
    expect(request.request.params.has('search')).toBe(false);

    request.flush({ items: [], page: 2, pageSize: 20, totalCount: 0, totalPages: 0, hasNextPage: false });
  });

  it('does not send a search parameter for whitespace', () => {
    service.list({ search: '   ' }).subscribe();

    const request = http.expectOne((candidate) => candidate.url === '/api/orders');
    expect(request.request.params.has('search')).toBe(false);

    request.flush({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0, hasNextPage: false });
  });

  it('surfaces a rejected transition as an ApiError carrying the legal alternatives', () => {
    let caught: ApiError | undefined;

    service.changeStatus('11111111-1111-1111-1111-111111111111', 'Fulfilled').subscribe({
      error: (error: ApiError) => (caught = error),
    });

    http.expectOne('/api/orders/11111111-1111-1111-1111-111111111111/status').flush(
      {
        title: 'That status change is not allowed',
        detail: 'A Pending order cannot move to Fulfilled. Allowed: Confirmed, Cancelled.',
        code: 'INVALID_STATUS_TRANSITION',
        allowedTransitions: ['Confirmed', 'Cancelled'],
      },
      { status: 409, statusText: 'Conflict' },
    );

    expect(caught).toBeInstanceOf(ApiError);
    expect(caught!.code).toBe('INVALID_STATUS_TRANSITION');
    expect(caught!.allowedTransitions).toEqual(['Confirmed', 'Cancelled']);
    expect(caught!.message).toContain('Confirmed');
  });

  it('turns a validation failure into per-field messages', () => {
    let caught: ApiError | undefined;

    service.submit(aRequest()).subscribe({ error: (error: ApiError) => (caught = error) });

    http.expectOne('/api/orders').flush(
      {
        title: 'One or more fields need attention',
        code: 'VALIDATION_FAILED',
        errors: { 'lines[0].quantity': ['Quantity must be at least 1.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(caught!.fieldErrors['lines[0].quantity']).toEqual(['Quantity must be at least 1.']);
  });

  it('explains an unreachable API rather than calling it a server error', () => {
    let caught: ApiError | undefined;

    service.list().subscribe({ error: (error: ApiError) => (caught = error) });

    http
      .expectOne((candidate) => candidate.url === '/api/orders')
      .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

    expect(caught!.code).toBe('NETWORK_UNREACHABLE');
  });

  it('treats a status change that changed nothing as a success', () => {
    let changed: boolean | undefined;

    service
      .changeStatus('11111111-1111-1111-1111-111111111111', 'Pending')
      .subscribe((result) => (changed = result.changed));

    http
      .expectOne('/api/orders/11111111-1111-1111-1111-111111111111/status')
      .flush(anOrder(), { status: 200, statusText: 'OK', headers: { 'X-Status-Changed': 'false' } });

    expect(changed).toBe(false);
  });
});
