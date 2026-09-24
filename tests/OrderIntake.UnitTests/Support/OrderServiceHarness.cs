using Microsoft.Extensions.Logging.Abstractions;
using OrderIntake.Application.Abstractions;
using OrderIntake.Application.Orders;
using OrderIntake.Application.Orders.Validation;
using OrderIntake.Infrastructure.Idempotency;

namespace OrderIntake.UnitTests.Support;

/// <summary>
/// Wires up an OrderService with test doubles.
///
/// The validator is the real one rather than a stub: validation is part of the
/// behaviour under test, and a stub would let a broken rule pass unnoticed.
/// The gate defaults to the real KeyedIdempotencyGate for the same reason —
/// tests that want to prove what happens *without* it opt out explicitly.
/// </summary>
public sealed class OrderServiceHarness
{
    public OrderServiceHarness(
        bool enforceUniqueIndex = true,
        bool useRealGate = true,
        TimeSpan? writeLatency = null)
    {
        Orders = new FakeOrderRepository
        {
            EnforceUniqueIndex = enforceUniqueIndex,
            WriteLatency = writeLatency ?? TimeSpan.Zero
        };

        Customers = new FakeCustomerRepository();
        Clock = new FixedClock();
        Gate = useRealGate ? new KeyedIdempotencyGate() : new NoOpIdempotencyGate();

        Service = new OrderService(
            Orders,
            Customers,
            Gate,
            Clock,
            new SubmitOrderRequestValidator(),
            NullLogger<OrderService>.Instance);
    }

    public FakeOrderRepository Orders { get; }

    public FakeCustomerRepository Customers { get; }

    public FixedClock Clock { get; }

    public IIdempotencyGate Gate { get; }

    public OrderService Service { get; }
}
