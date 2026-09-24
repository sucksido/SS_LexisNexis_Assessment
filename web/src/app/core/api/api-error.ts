import { HttpErrorResponse } from '@angular/common/http';

import { OrderStatus } from '../models/order.models';

/**
 * The RFC 7807 body the API returns, plus the extensions this service adds.
 * See `OrderIntake.Api/Infrastructure/GlobalExceptionHandler.cs`.
 */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  traceId?: string;

  /** Stable machine-readable code, e.g. `REFERENCE_REUSED`. */
  code?: string;

  /** Field name (camelCased, matching the JSON we sent) to messages. */
  errors?: Record<string, string[]>;

  currentStatus?: OrderStatus;
  requestedStatus?: OrderStatus;
  allowedTransitions?: OrderStatus[];

  externalReference?: string;
  existingOrderId?: string;
}

/**
 * A failed call, normalised.
 *
 * Components branch on `code`, never on `message`. A code is a contract; a
 * message is prose, and prose gets rewritten the first time someone fixes a
 * typo in it.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
    readonly problem: ProblemDetails = {},
  ) {
    super(message);
    this.name = 'ApiError';

    // Required when a class extends a built-in and the code is transpiled down;
    // without it `instanceof ApiError` silently returns false.
    Object.setPrototypeOf(this, ApiError.prototype);
  }

  /** Per-field validation messages, empty when the failure was not a 400. */
  get fieldErrors(): Record<string, string[]> {
    return this.problem.errors ?? {};
  }

  /** Set on a rejected status change: what the order may do instead. */
  get allowedTransitions(): OrderStatus[] {
    return this.problem.allowedTransitions ?? [];
  }

  /** Set on a reference clash: the order already holding the reference. */
  get existingOrderId(): string | null {
    return this.problem.existingOrderId ?? null;
  }

  static from(error: unknown): ApiError {
    if (error instanceof ApiError) {
      return error;
    }

    if (!(error instanceof HttpErrorResponse)) {
      return new ApiError(0, 'UNKNOWN', 'Something went wrong. Please try again.');
    }

    // status 0 means the request never reached the server: the API is down, the
    // proxy is misconfigured, or CORS rejected it. Saying "server error" here
    // would send the reader looking in the wrong logs.
    if (error.status === 0) {
      return new ApiError(
        0,
        'NETWORK_UNREACHABLE',
        'Could not reach the API. Is the backend running on http://localhost:5080?',
      );
    }

    const problem: ProblemDetails =
      error.error && typeof error.error === 'object' ? (error.error as ProblemDetails) : {};

    const message =
      problem.detail?.trim() ||
      problem.title?.trim() ||
      error.message ||
      `Request failed with status ${error.status}.`;

    return new ApiError(error.status, problem.code ?? `HTTP_${error.status}`, message, problem);
  }
}
