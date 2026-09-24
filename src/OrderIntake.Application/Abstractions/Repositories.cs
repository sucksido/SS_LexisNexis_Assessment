using OrderIntake.Application.Orders.Contracts;
using OrderIntake.Domain.Customers;
using OrderIntake.Domain.Orders;

namespace OrderIntake.Application.Abstractions;

/// <summary>
/// Persistence contract for the Order aggregate.
///
/// Returns domain objects, takes domain objects, and translates store-specific
/// failures (such as a unique-index violation) into
/// <see cref="Common.DuplicateReferenceException"/>. That translation is the whole
/// point of the interface: the application layer can handle a lost race without
/// referencing EF Core, and the unit tests can substitute a dictionary.
/// </summary>
public interface IOrderRepository
{
    /// <summary>Looks up an order by the idempotency key: customer + their reference.</summary>
    Task<Order?> FindByReferenceAsync(Guid customerId, string externalReference, CancellationToken cancellationToken);

    /// <summary>Loads a single order with its lines.</summary>
    Task<Order?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists orders newest-first, with optional status and free-text filtering.
    /// Filtering and paging happen in the store, not in memory, so this keeps
    /// working when the table stops being small.
    /// </summary>
    Task<PagedResult<OrderSummary>> ListAsync(ListOrdersQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new order.
    /// </summary>
    /// <exception cref="Common.DuplicateReferenceException">
    /// The (customer, reference) pair was already taken — typically because a
    /// concurrent request won the race.
    /// </exception>
    Task AddAsync(Order order, CancellationToken cancellationToken);

    /// <summary>Persists changes made to an already-tracked order.</summary>
    Task UpdateAsync(Order order, CancellationToken cancellationToken);

    /// <summary>
    /// Every reference beginning with the given prefix, across all customers.
    ///
    /// Exists solely so <see cref="IOrderReferenceSequence"/> can work out where
    /// to resume after a restart. Deliberately narrow: it returns strings rather
    /// than orders so the caller cannot quietly turn it into a general-purpose
    /// query, and it is the only method here that is not scoped to a customer —
    /// because a reference the UI hands out must not collide with *anyone's*.
    /// </summary>
    Task<IReadOnlyList<string>> ListReferencesStartingWithAsync(
        string prefix,
        CancellationToken cancellationToken);
}

/// <summary>
/// Hands out the next order reference for the UI to display.
///
/// A deliberate change of meaning from the original design, and the trade is
/// worth stating. The reference used to be the rep's own PO number, copied off
/// the customer's paperwork; now the system issues it. That removes a class of
/// human error (two reps typing the same number, or one rep typing a different
/// number for the same order) at the cost of no longer matching an external
/// document.
///
/// What it is *not* is a source of uniqueness. The number is issued when the
/// form is opened and used when it is submitted, so it is a reservation nobody
/// enforces: abandoned forms leave gaps, and across multiple API instances two
/// reps can be handed the same number. The unique index remains the only
/// guarantee — exactly as it was when reps typed the reference themselves.
/// </summary>
public interface IOrderReferenceSequence
{
    Task<string> NextAsync(CancellationToken cancellationToken);
}

public interface ICustomerRepository
{
    /// <summary>
    /// Finds a customer by normalised email, or creates one.
    ///
    /// Customers are identified by email rather than by a surrogate the caller
    /// supplies, because the sales rep filling in the form knows the email and
    /// does not know our internal id.
    /// </summary>
    Task<Customer> GetOrCreateAsync(string email, string name, CancellationToken cancellationToken);

    Task<Customer?> GetByIdAsync(Guid customerId, CancellationToken cancellationToken);
}

/// <summary>
/// Abstraction over "what time is it", so that time-dependent behaviour is
/// assertable in tests instead of being a source of flakiness.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// A named, process-wide mutual-exclusion gate.
///
/// Used to serialise concurrent submissions that share an idempotency key. It is
/// a latency optimisation and a correctness backstop for stores that do not
/// enforce unique indexes (EF Core's in-memory provider being exactly that) —
/// it is explicitly *not* the only defence. See SOLUTION.md.
/// </summary>
public interface IIdempotencyGate
{
    Task<IAsyncDisposable> AcquireAsync(string key, CancellationToken cancellationToken);
}
