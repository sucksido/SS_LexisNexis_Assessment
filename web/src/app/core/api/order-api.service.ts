import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, throwError } from 'rxjs';

import {
  ChangeStatusResult,
  ListOrdersQuery,
  NextReferenceResponse,
  OrderResponse,
  OrderStatus,
  OrderSummary,
  PagedResult,
  StatusDescriptor,
  SubmitOrderRequest,
  SubmitOrderResult,
} from '../models/order.models';
import { ApiError } from './api-error';

/**
 * The only place in the app that knows about HTTP.
 *
 * Components receive domain-shaped results and `ApiError`s; they never see an
 * `HttpErrorResponse`, a status code or a header name. That boundary is what
 * keeps the components readable and testable with a plain stub.
 */
@Injectable({ providedIn: 'root' })
export class OrderApiService {
  /**
   * Relative on purpose. In development `proxy.conf.json` forwards /api to the
   * API on :5080; in the container nginx does the same. Either way the browser
   * only ever talks to its own origin, so there is no CORS preflight in the hot
   * path and no build-time base URL to get wrong per environment.
   */
  private readonly baseUrl = '/api/orders';

  private readonly http = inject(HttpClient);

  /**
   * Submits an order.
   *
   * The interesting part is the return: the server answers 201 for a new order
   * and 200 for a replay of one it has already recorded. Both are successes, so
   * this resolves either way, and the caller is told which happened rather than
   * being left to guess.
   */
  submit(request: SubmitOrderRequest): Observable<SubmitOrderResult> {
    return this.http
      .post<OrderResponse>(this.baseUrl, request, { observe: 'response' })
      .pipe(
        map((response) => ({
          order: response.body as OrderResponse,
          // The header is authoritative (the API sets it explicitly); the status
          // code is the fallback for any proxy that strips custom headers.
          isReplay:
            response.headers.get('X-Idempotent-Replay') === 'true' || response.status === 200,
        })),
        catchError(this.toApiError),
      );
  }

  /** Lists orders, newest first. Filtering and paging happen on the server. */
  list(query: ListOrdersQuery = {}): Observable<PagedResult<OrderSummary>> {
    let params = new HttpParams()
      .set('page', String(query.page ?? 1))
      .set('pageSize', String(query.pageSize ?? 20));

    if (query.status) {
      params = params.set('status', query.status);
    }

    // An empty search box means "no filter", not "match the empty string".
    const search = query.search?.trim();
    if (search) {
      params = params.set('search', search);
    }

    return this.http
      .get<PagedResult<OrderSummary>>(this.baseUrl, { params })
      .pipe(catchError(this.toApiError));
  }

  get(orderId: string): Observable<OrderResponse> {
    return this.http
      .get<OrderResponse>(`${this.baseUrl}/${orderId}`)
      .pipe(catchError(this.toApiError));
  }

  /**
   * Moves an order to a new status.
   *
   * `changed` is false when the order was already in that status — a success,
   * but one worth distinguishing so the UI can say "already confirmed" instead
   * of flashing a misleading "updated".
   */
  changeStatus(orderId: string, status: OrderStatus): Observable<ChangeStatusResult> {
    return this.http
      .put<OrderResponse>(
        `${this.baseUrl}/${orderId}/status`,
        { status },
        { observe: 'response' },
      )
      .pipe(
        map((response) => ({
          order: response.body as OrderResponse,
          changed: response.headers.get('X-Status-Changed') !== 'false',
        })),
        catchError(this.toApiError),
      );
  }

  /**
   * Asks the server for the reference to show on a new order form.
   *
   * Unwrapped to a bare string here rather than passed through as a DTO: the
   * caller wants a reference, and the envelope is a transport detail that has no
   * business reaching a component.
   */
  nextReference(): Observable<string> {
    return this.http
      .get<NextReferenceResponse>(`${this.baseUrl}/next-reference`)
      .pipe(
        map((response) => response.externalReference),
        catchError(this.toApiError),
      );
  }

  /** The status graph, so the UI can describe the workflow without owning it. */
  statuses(): Observable<StatusDescriptor[]> {
    return this.http
      .get<StatusDescriptor[]>(`${this.baseUrl}/statuses`)
      .pipe(catchError(this.toApiError));
  }

  private readonly toApiError = (error: unknown): Observable<never> =>
    throwError(() => ApiError.from(error));
}
