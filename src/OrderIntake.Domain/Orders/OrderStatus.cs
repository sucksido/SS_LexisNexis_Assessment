namespace OrderIntake.Domain.Orders;

/// <summary>
/// The lifecycle of an order. Persisted as a string (see DbContext configuration)
/// so that adding or reordering members never silently rewrites existing rows.
/// </summary>
public enum OrderStatus
{
    Pending = 0,
    Confirmed = 1,
    Fulfilled = 2,
    Cancelled = 3
}
