using OrderIntake.Domain.Orders;

namespace OrderIntake.Application.Orders.Contracts;

// ---------------------------------------------------------------------------
// Inbound
// ---------------------------------------------------------------------------

/// <summary>
/// A purchase order as submitted by the Angular client.
///
/// Note what is absent: no totals, no line totals, no status. Those are the
/// server's to decide. Accepting them would mean either trusting the client's
/// arithmetic or silently discarding fields the client thought mattered.
/// </summary>
public sealed record SubmitOrderRequest
{
    /// <summary>
    /// The caller's reference for this order, and the idempotency key.
    ///
    /// The intake form fetches one from <c>GET /api/orders/next-reference</c> and
    /// shows it read-only, but any non-empty string is accepted so an import job
    /// or a partner integration can supply its own PO number.
    /// </summary>
    public string ExternalReference { get; init; } = string.Empty;

    public CustomerRequest Customer { get; init; } = new();

    public string Currency { get; init; } = "USD";

    public string? Notes { get; init; }

    public IReadOnlyList<OrderLineRequest> Lines { get; init; } = Array.Empty<OrderLineRequest>();
}

public sealed record CustomerRequest
{
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public sealed record OrderLineRequest
{
    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}

public sealed record ChangeOrderStatusRequest
{
    public OrderStatus Status { get; init; }
}

/// <summary>Filter, sort and paging options for the order list.</summary>
public sealed record ListOrdersQuery
{
    public const int MaxPageSize = 100;

    private readonly int _page = 1;
    private readonly int _pageSize = 20;

    public int Page
    {
        get => _page;
        init => _page = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        init => _pageSize = value switch
        {
            < 1 => 20,
            > MaxPageSize => MaxPageSize,
            _ => value
        };
    }

    /// <summary>Optional status filter. Null means "all statuses".</summary>
    public OrderStatus? Status { get; init; }

    /// <summary>Optional free-text match against reference, customer name or email.</summary>
    public string? Search { get; init; }
}

// ---------------------------------------------------------------------------
// Outbound
// ---------------------------------------------------------------------------

public sealed record OrderResponse
{
    public required Guid Id { get; init; }
    public required string ExternalReference { get; init; }
    public required CustomerResponse Customer { get; init; }
    public required string Status { get; init; }
    public required string Currency { get; init; }
    public string? Notes { get; init; }
    public required decimal Subtotal { get; init; }
    public required decimal Total { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required IReadOnlyList<OrderLineResponse> Lines { get; init; }

    /// <summary>
    /// The statuses this order may move to next.
    ///
    /// Sent on every response so the UI renders exactly the buttons that will
    /// work. Without it the client has to duplicate the transition rules, and
    /// two copies of a rule is one copy too many.
    /// </summary>
    public required IReadOnlyList<string> AllowedTransitions { get; init; }
}

public sealed record CustomerResponse
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string Name { get; init; }
}

public sealed record OrderLineResponse
{
    public required Guid Id { get; init; }
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public required int Quantity { get; init; }
    public required decimal UnitPrice { get; init; }
    public required decimal LineTotal { get; init; }
}

/// <summary>Row shape for the list screen — deliberately without line items.</summary>
public sealed record OrderSummary
{
    public required Guid Id { get; init; }
    public required string ExternalReference { get; init; }
    public required string CustomerName { get; init; }
    public required string CustomerEmail { get; init; }
    public required string Status { get; init; }
    public required string Currency { get; init; }
    public required decimal Total { get; init; }
    public required int LineCount { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalCount { get; init; }

    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasNextPage => Page < TotalPages;
}

/// <summary>
/// Outcome of a submission, so the API can answer 201 Created for a genuinely
/// new order and 200 OK for a replay of one it has already seen. The status code
/// is the honest signal here; the body is identical either way.
/// </summary>
public sealed record SubmitOrderResult
{
    public required OrderResponse Order { get; init; }

    /// <summary>True when this submission matched an order that already existed.</summary>
    public required bool IsReplay { get; init; }
}

/// <summary>
/// Outcome of a status change. <see cref="Changed"/> is false when the order was
/// already in the requested status — a success, but one worth reporting so the
/// UI can say "already confirmed" rather than flashing a misleading "updated".
/// </summary>
public sealed record ChangeOrderStatusResult
{
    public required OrderResponse Order { get; init; }
    public required bool Changed { get; init; }
}
