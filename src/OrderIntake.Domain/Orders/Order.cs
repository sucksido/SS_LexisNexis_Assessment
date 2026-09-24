using OrderIntake.Domain.Common;

namespace OrderIntake.Domain.Orders;

/// <summary>
/// Result of asking an order to change status, so callers can tell a real
/// transition apart from a harmless repeat of the current status.
/// </summary>
public enum StatusChangeOutcome
{
    /// <summary>The order moved to a new status.</summary>
    Changed,

    /// <summary>The order was already in the requested status; nothing was written.</summary>
    AlreadyInStatus
}

/// <summary>
/// The Order aggregate root.
///
/// Every invariant that matters lives in here rather than in the service layer:
/// an Order that exists is an Order that is valid. Collections and setters are
/// private so the only ways to produce one are <see cref="Create"/> and EF Core's
/// materialiser, and the only way to move it through its lifecycle is
/// <see cref="ChangeStatus"/>.
/// </summary>
public sealed class Order
{
    private readonly List<OrderLine> _lines = new();

    // Required by EF Core's materialiser.
    private Order()
    {
    }

    private Order(
        Guid customerId,
        string externalReference,
        string currency,
        string? notes,
        IReadOnlyCollection<OrderLineDraft> lines,
        string requestFingerprint,
        DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        CustomerId = customerId;
        ExternalReference = NormaliseReference(externalReference);
        Currency = Money.NormaliseCurrency(currency);
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        RequestFingerprint = requestFingerprint;

        Status = OrderStatus.Pending;
        CreatedAtUtc = now.ToUniversalTime();
        UpdatedAtUtc = CreatedAtUtc;

        if (lines.Count == 0)
        {
            throw new DomainValidationException("An order must contain at least one line item.");
        }

        foreach (var line in lines)
        {
            _lines.Add(new OrderLine(line.Sku, line.Name, line.Quantity, line.UnitPrice));
        }

        GuardAgainstDuplicateSkus();
        RecalculateTotals();
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// The caller's reference for this order. The intake form now has the server
    /// issue it (PO-000042 and upward) rather than letting a rep type one, but
    /// the domain deliberately does not care where it came from: the API accepts
    /// any non-empty string, so an import job or a partner integration can still
    /// bring its own.
    ///
    /// Doubles as the idempotency key, scoped to <see cref="CustomerId"/> — two
    /// different customers are both entitled to have a "PO-1001".
    /// </summary>
    public string ExternalReference { get; private set; } = null!;

    public Guid CustomerId { get; private set; }

    /// <summary>
    /// Hash of the submitted payload. Lets a replay of the *same* request return
    /// the original order, while a different payload reusing the same reference
    /// is reported as a conflict instead of being silently ignored.
    /// </summary>
    public string RequestFingerprint { get; private set; } = null!;

    public string Currency { get; private set; } = null!;
    public string? Notes { get; private set; }
    public OrderStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public decimal Subtotal { get; private set; }
    public decimal Total { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    /// <summary>Convenience for the API: what this order is allowed to become next.</summary>
    public IReadOnlyList<OrderStatus> AllowedTransitions => OrderStatusPolicy.AllowedTransitionsFrom(Status);

    public static Order Create(
        Guid customerId,
        string externalReference,
        string currency,
        string? notes,
        IReadOnlyCollection<OrderLineDraft> lines,
        string requestFingerprint,
        DateTimeOffset now) =>
        new(customerId, externalReference, currency, notes, lines, requestFingerprint, now);

    /// <summary>
    /// Moves the order to <paramref name="target"/> if the policy allows it.
    ///
    /// Asking for the status the order is already in is treated as a success with
    /// no write, not an error. A rep double-clicking "Confirm" should not see a
    /// failure for an outcome that already holds — the same reasoning that drives
    /// the duplicate-submission handling.
    /// </summary>
    /// <exception cref="InvalidStatusTransitionException">The transition is not permitted.</exception>
    public StatusChangeOutcome ChangeStatus(OrderStatus target, DateTimeOffset now)
    {
        if (target == Status)
        {
            return StatusChangeOutcome.AlreadyInStatus;
        }

        if (!OrderStatusPolicy.CanTransition(Status, target))
        {
            throw new InvalidStatusTransitionException(Status, target);
        }

        Status = target;
        UpdatedAtUtc = now.ToUniversalTime();

        return StatusChangeOutcome.Changed;
    }

    /// <summary>
    /// The subtotal is the sum of the line totals, and because unit prices are
    /// held to whole cents and quantities are whole numbers, that sum is exact:
    /// every row multiplies out without a remainder and there is no rounding
    /// drift between the rows and the figure at the bottom. Someone checking the
    /// order by hand gets the same answer.
    ///
    /// The Round call is therefore a no-op today, kept for the same reason as the
    /// one in OrderLine: the first discount, tax rate or currency conversion makes
    /// it load-bearing. See MoneyTests for the behaviour it is holding.
    ///
    /// Total equals subtotal today. It is kept as its own property because tax,
    /// shipping and discounts land on the total, not the subtotal, and collapsing
    /// them now would mean a schema change later.
    /// </summary>
    private void RecalculateTotals()
    {
        Subtotal = Money.Round(_lines.Sum(line => line.LineTotal));
        Total = Subtotal;
    }

    private void GuardAgainstDuplicateSkus()
    {
        var duplicate = _lines
            .GroupBy(line => line.Sku, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new DomainValidationException(
                $"SKU '{duplicate.Key}' appears on more than one line. " +
                "Combine them into a single line with the total quantity.");
        }
    }

    private static string NormaliseReference(string? reference)
    {
        var trimmed = (reference ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new DomainValidationException("An external reference is required.");
        }

        if (trimmed.Length > 64)
        {
            throw new DomainValidationException(
                $"External reference must be 64 characters or fewer, but was {trimmed.Length}.");
        }

        return trimmed;
    }
}
