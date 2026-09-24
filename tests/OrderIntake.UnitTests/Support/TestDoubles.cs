using System.Collections.Concurrent;
using OrderIntake.Application.Abstractions;
using OrderIntake.Application.Common;
using OrderIntake.Application.Orders.Contracts;
using OrderIntake.Domain.Customers;
using OrderIntake.Domain.Orders;

namespace OrderIntake.UnitTests.Support;

public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset? now = null) =>
        UtcNow = now ?? new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

/// <summary>
/// A gate that does nothing, used to isolate the other two defences and prove
/// they work on their own.
/// </summary>
public sealed class NoOpIdempotencyGate : IIdempotencyGate
{
    private sealed class NullLease : IAsyncDisposable
    {
        public static readonly NullLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public Task<IAsyncDisposable> AcquireAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<IAsyncDisposable>(NullLease.Instance);
}

/// <summary>
/// An in-memory order store with two switches that let a test choose which
/// failure mode it is exercising.
///
/// <see cref="EnforceUniqueIndex"/> false models EF Core's in-memory provider,
/// which accepts a declared unique index and then cheerfully ignores it. True
/// models a real database.
///
/// <see cref="WriteLatency"/> widens the window between the read and the write so
/// a race is reproduced reliably rather than once in a hundred runs.
/// </summary>
public sealed class FakeOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();
    private readonly object _writeLock = new();
    private int _addAttempts;

    public bool EnforceUniqueIndex { get; init; } = true;

    public TimeSpan WriteLatency { get; init; } = TimeSpan.Zero;

    /// <summary>How many times the service tried to insert. Distinguishes
    /// "we never raced" from "we raced and recovered".</summary>
    public int AddAttempts => Volatile.Read(ref _addAttempts);

    public int Count => _orders.Count;

    public IReadOnlyCollection<Order> All => _orders.Values.ToList();

    public Task<Order?> FindByReferenceAsync(
        Guid customerId,
        string externalReference,
        CancellationToken cancellationToken) =>
        Task.FromResult(_orders.Values.FirstOrDefault(order =>
            order.CustomerId == customerId &&
            order.ExternalReference == externalReference));

    public Task<Order?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(_orders.TryGetValue(orderId, out var order) ? order : null);

    public async Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _addAttempts);

        if (WriteLatency > TimeSpan.Zero)
        {
            await Task.Delay(WriteLatency, cancellationToken);
        }

        lock (_writeLock)
        {
            if (EnforceUniqueIndex && _orders.Values.Any(existing =>
                    existing.CustomerId == order.CustomerId &&
                    existing.ExternalReference == order.ExternalReference))
            {
                throw new DuplicateReferenceException(order.ExternalReference);
            }

            _orders[order.Id] = order;
        }
    }

    public Task UpdateAsync(Order order, CancellationToken cancellationToken)
    {
        _orders[order.Id] = order;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListReferencesStartingWithAsync(
        string prefix,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(_orders.Values
            .Select(order => order.ExternalReference)
            .Where(reference => reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList());

    public Task<PagedResult<OrderSummary>> ListAsync(
        ListOrdersQuery query,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "Listing is exercised against the real EF query in EfOrderRepositoryTests, " +
            "because a hand-written fake would only ever prove that the fake sorts correctly.");
}

public sealed class FakeCustomerRepository : ICustomerRepository
{
    private readonly ConcurrentDictionary<string, Customer> _byEmail = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Customer> _byId = new();

    public int Count => _byEmail.Count;

    public Task<Customer> GetOrCreateAsync(string email, string name, CancellationToken cancellationToken)
    {
        var normalised = Customer.NormaliseEmail(email);

        var customer = _byEmail.GetOrAdd(normalised, key => Customer.Create(key, name));
        _byId[customer.Id] = customer;

        return Task.FromResult(customer);
    }

    public Task<Customer?> GetByIdAsync(Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.TryGetValue(customerId, out var customer) ? customer : null);
}
