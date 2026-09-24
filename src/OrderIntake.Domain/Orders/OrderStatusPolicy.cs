namespace OrderIntake.Domain.Orders;

/// <summary>
/// The single source of truth for which status changes are legal.
///
/// Keeping this as data (a map) rather than a chain of if-statements means the
/// API can *expose* the allowed next steps (see <see cref="AllowedTransitionsFrom"/>),
/// which is what lets the Angular UI render only the buttons that will actually
/// succeed instead of guessing and handling a 409.
/// </summary>
public static class OrderStatusPolicy
{
    private static readonly IReadOnlyDictionary<OrderStatus, IReadOnlyList<OrderStatus>> Map =
        new Dictionary<OrderStatus, IReadOnlyList<OrderStatus>>
        {
            [OrderStatus.Pending] = new[] { OrderStatus.Confirmed, OrderStatus.Cancelled },
            [OrderStatus.Confirmed] = new[] { OrderStatus.Fulfilled, OrderStatus.Cancelled },

            // Terminal states. A fulfilled order has left the building; a cancelled
            // order must be re-submitted as a new order rather than resurrected,
            // so that the audit trail stays honest.
            [OrderStatus.Fulfilled] = Array.Empty<OrderStatus>(),
            [OrderStatus.Cancelled] = Array.Empty<OrderStatus>()
        };

    /// <summary>The statuses an order in <paramref name="current"/> may legally move to.</summary>
    public static IReadOnlyList<OrderStatus> AllowedTransitionsFrom(OrderStatus current) =>
        Map.TryGetValue(current, out var allowed) ? allowed : Array.Empty<OrderStatus>();

    public static bool CanTransition(OrderStatus current, OrderStatus target) =>
        AllowedTransitionsFrom(current).Contains(target);

    public static bool IsTerminal(OrderStatus status) => AllowedTransitionsFrom(status).Count == 0;
}

/// <summary>
/// Raised when a caller asks for a status change the policy forbids.
/// Carries the allowed alternatives so the error message can be genuinely helpful
/// ("Cancelled is terminal") rather than just "not allowed".
/// </summary>
public sealed class InvalidStatusTransitionException : Common.DomainException
{
    public InvalidStatusTransitionException(OrderStatus current, OrderStatus target)
        : base("INVALID_STATUS_TRANSITION", BuildMessage(current, target))
    {
        CurrentStatus = current;
        TargetStatus = target;
        AllowedTransitions = OrderStatusPolicy.AllowedTransitionsFrom(current);
    }

    public OrderStatus CurrentStatus { get; }
    public OrderStatus TargetStatus { get; }
    public IReadOnlyList<OrderStatus> AllowedTransitions { get; }

    private static string BuildMessage(OrderStatus current, OrderStatus target)
    {
        var allowed = OrderStatusPolicy.AllowedTransitionsFrom(current);

        return allowed.Count == 0
            ? $"This order is {current}, which is a final state. It can no longer be changed to {target}."
            : $"An order that is {current} cannot be changed to {target}. " +
              $"Allowed next steps are: {string.Join(", ", allowed)}.";
    }
}
