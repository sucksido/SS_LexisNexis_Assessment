/**
 * Client-side mirror of the server contracts in
 * `OrderIntake.Application/Orders/Contracts/OrderContracts.cs`.
 *
 * Hand-written rather than generated from the OpenAPI document. For a surface
 * this small the generator would add a build step, a checked-in pile of
 * generated code and a version to keep in step, to save writing sixty lines
 * once. On a larger API the trade would go the other way.
 */

export type OrderStatus = 'Pending' | 'Confirmed' | 'Fulfilled' | 'Cancelled';

/**
 * The currencies the intake form offers.
 *
 * A short hard-coded list, and that is the right call only while it stays
 * short. The server accepts any three-letter code, so this is a UI convenience
 * rather than a rule — the moment a rep needs a third currency, this becomes a
 * server-owned list served alongside the status graph, for the same reason the
 * status graph is server-owned.
 */
export const CURRENCIES = ['USD', 'ZAR'] as const;

export type Currency = (typeof CURRENCIES)[number];

export const ORDER_STATUSES: readonly OrderStatus[] = [
  'Pending',
  'Confirmed',
  'Fulfilled',
  'Cancelled',
] as const;

// ---------------------------------------------------------------------------
// Outbound (what we send)
// ---------------------------------------------------------------------------

export interface CustomerRequest {
  email: string;
  name: string;
}

export interface OrderLineRequest {
  sku: string;
  name: string;
  quantity: number;
  unitPrice: number;
}

export interface SubmitOrderRequest {
  externalReference: string;
  customer: CustomerRequest;
  currency: string;
  notes?: string | null;
  lines: OrderLineRequest[];
}

// ---------------------------------------------------------------------------
// Inbound (what we receive)
// ---------------------------------------------------------------------------

export interface CustomerResponse {
  id: string;
  email: string;
  name: string;
}

export interface OrderLineResponse {
  id: string;
  sku: string;
  name: string;
  quantity: number;
  unitPrice: number;
  /** Computed by the server. The client never calculates money. */
  lineTotal: number;
}

export interface OrderResponse {
  id: string;
  externalReference: string;
  customer: CustomerResponse;
  status: OrderStatus;
  currency: string;
  notes?: string | null;
  subtotal: number;
  total: number;
  createdAtUtc: string;
  updatedAtUtc: string;
  lines: OrderLineResponse[];

  /**
   * The statuses this order may move to next, decided by the domain.
   *
   * The UI renders one button per entry instead of keeping its own copy of the
   * transition rules. Two copies of a rule is one copy too many: the server
   * would reject a move the UI offered, and the rep would see an error for
   * doing exactly what the screen invited them to do.
   */
  allowedTransitions: OrderStatus[];
}

/** Row shape for the list screen — no line items, on purpose. */
export interface OrderSummary {
  id: string;
  externalReference: string;
  customerName: string;
  customerEmail: string;
  status: OrderStatus;
  currency: string;
  total: number;
  lineCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasNextPage: boolean;
}

export interface ListOrdersQuery {
  page?: number;
  pageSize?: number;
  status?: OrderStatus | null;
  search?: string | null;
}

/** The reply from `GET /api/orders/next-reference`. */
export interface NextReferenceResponse {
  externalReference: string;
}

/** One node of the status graph, from `GET /api/orders/statuses`. */
export interface StatusDescriptor {
  status: OrderStatus;
  allowedTransitions: OrderStatus[];
  isTerminal: boolean;
}

// ---------------------------------------------------------------------------
// Results
// ---------------------------------------------------------------------------

/**
 * A submission outcome. `isReplay` comes from the HTTP status (201 vs 200),
 * corroborated by the `X-Idempotent-Replay` header, so the UI can say
 * "you had already sent this one" rather than pretending a new order appeared.
 */
export interface SubmitOrderResult {
  order: OrderResponse;
  isReplay: boolean;
}

export interface ChangeStatusResult {
  order: OrderResponse;
  changed: boolean;
}
