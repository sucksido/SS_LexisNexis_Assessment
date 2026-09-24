using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using OrderIntake.Application.Abstractions;
using OrderIntake.Application.Common;
using OrderIntake.Application.Orders;
using OrderIntake.Application.Orders.Contracts;
using OrderIntake.Application.Orders.Validation;
using OrderIntake.Domain.Orders;
using OrderIntake.UnitTests.Support;

namespace OrderIntake.UnitTests.Application;

/// <summary>
/// The behaviour the brief is actually about: a sales rep who is not sure whether
/// their order went through clicks Submit again.
/// </summary>
public sealed class OrderSubmissionTests
{
    [Fact]
    public async Task A_first_submission_creates_an_order()
    {
        var harness = new OrderServiceHarness();

        var result = await harness.Service.SubmitAsync(
            OrderRequestBuilder.AnOrder().Build(), CancellationToken.None);

        result.IsReplay.Should().BeFalse();
        result.Order.Status.Should().Be("Pending");
        result.Order.Total.Should().Be(253.48m);
        harness.Orders.Count.Should().Be(1);
    }

    [Fact]
    public async Task Submitting_the_same_thing_twice_returns_the_same_order_and_creates_only_one()
    {
        var harness = new OrderServiceHarness();
        var request = OrderRequestBuilder.AnOrder().Build();

        var first = await harness.Service.SubmitAsync(request, CancellationToken.None);
        var second = await harness.Service.SubmitAsync(request, CancellationToken.None);

        first.IsReplay.Should().BeFalse();
        second.IsReplay.Should().BeTrue("the second call matched an order we already had");

        second.Order.Id.Should().Be(first.Order.Id);
        second.Order.Total.Should().Be(first.Order.Total);
        second.Order.CreatedAtUtc.Should().Be(first.Order.CreatedAtUtc);

        harness.Orders.Count.Should().Be(1);
    }

    /// <summary>
    /// The same basket typed in a different order, with the notes tidied up, is
    /// still the same order. Being too literal here would turn a helpful retry
    /// into a conflict the rep cannot do anything about.
    /// </summary>
    [Fact]
    public async Task Cosmetic_differences_still_count_as_the_same_order()
    {
        var harness = new OrderServiceHarness();

        var first = await harness.Service.SubmitAsync(
            OrderRequestBuilder.AnOrder().WithNotes("Frist attempt").Build(),
            CancellationToken.None);

        var second = await harness.Service.SubmitAsync(
            OrderRequestBuilder.AnOrder().WithNotes("First attempt").WithLinesReversed().Build(),
            CancellationToken.None);

        second.IsReplay.Should().BeTrue();
        second.Order.Id.Should().Be(first.Order.Id);
        harness.Orders.Count.Should().Be(1);
    }

    /// <summary>
    /// The case where silently returning the original would be actively harmful:
    /// the rep changed the quantities and would walk away believing it saved.
    /// </summary>
    [Fact]
    public async Task Reusing_a_reference_for_different_content_is_refused()
    {
        var harness = new OrderServiceHarness();

        var original = await harness.Service.SubmitAsync(
            OrderRequestBuilder.AnOrder().WithLine("KB-001", 2, 89.99m).Build(),
            CancellationToken.None);

        var act = async () => await harness.Service.SubmitAsync(
            OrderRequestBuilder.AnOrder().WithLine("KB-001", 20, 89.99m).Build(),
            CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<ReferenceReusedException>()).Which;

        exception.Code.Should().Be("REFERENCE_REUSED_WITH_DIFFERENT_CONTENT");
        exception.ExistingOrderId.Should().Be(original.Order.Id);

        harness.Orders.Count.Should().Be(1, "the conflicting submission must not be stored");
        harness.Orders.All.Single().Lines.Single().Quantity.Should().Be(2, "the original is unchanged");
    }

    /// <summary>
    /// Idempotency is scoped to the customer. Two customers may each issue their
    /// own PO-1001, and neither should block the other.
    /// </summary>
    [Fact]
    public async Task The_same_reference_from_two_customers_is_two_orders()
    {
        var harness = new OrderServiceHarness();

        var first = await harness.Service.SubmitAsync(
            OrderRequestBuilder.AnOrder().WithReference("PO-1001").ForCustomer("ada@contoso.com").Build(),
            CancellationToken.None);

        var second = await harness.Service.SubmitAsync(
            OrderRequestBuilder.AnOrder().WithReference("PO-1001").ForCustomer("grace@contoso.com").Build(),
            CancellationToken.None);

        second.IsReplay.Should().BeFalse();
        second.Order.Id.Should().NotBe(first.Order.Id);
        harness.Orders.Count.Should().Be(2);
        harness.Customers.Count.Should().Be(2);
    }

    [Fact]
    public async Task A_customer_is_matched_by_email_regardless_of_casing()
    {
        var harness = new OrderServiceHarness();

        await harness.Service.SubmitAsync(
            OrderRequestBuilder.AnOrder().WithReference("PO-1").ForCustomer("Ada@Contoso.com").Build(),
            CancellationToken.None);

        var second = await harness.Service.SubmitAsync(
            OrderRequestBuilder.AnOrder().WithReference("PO-1").ForCustomer("ada@contoso.com").Build(),
            CancellationToken.None);

        second.IsReplay.Should().BeTrue();
        harness.Customers.Count.Should().Be(1);
        harness.Orders.Count.Should().Be(1);
    }

    // -----------------------------------------------------------------------
    // Concurrency
    // -----------------------------------------------------------------------

    /// <summary>
    /// The real-world failure: an impatient rep clicks Submit repeatedly on a
    /// slow connection, so N identical requests are in flight at once.
    ///
    /// The store here deliberately does NOT enforce a unique index — it models
    /// EF Core's in-memory provider, which is the default configuration. So this
    /// test proves that the gate alone holds the line when the database will not.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(25)]
    public async Task Concurrent_identical_submissions_create_exactly_one_order(int concurrency)
    {
        var harness = new OrderServiceHarness(
            enforceUniqueIndex: false,
            useRealGate: true,
            // Widens the read-to-write window so the race happens every run
            // rather than one run in a hundred.
            writeLatency: TimeSpan.FromMilliseconds(15));

        var request = OrderRequestBuilder.AnOrder().Build();

        var results = await Task.WhenAll(
            Enumerable.Range(0, concurrency)
                .Select(_ => Task.Run(() => harness.Service.SubmitAsync(request, CancellationToken.None))));

        harness.Orders.Count.Should().Be(1, "duplicate prevention is the point of the feature");
        harness.Orders.AddAttempts.Should().Be(1, "only the first caller through the gate should insert");

        results.Should().AllSatisfy(result =>
            result.Order.Id.Should().Be(results[0].Order.Id, "every caller sees the same order"));

        results.Count(result => !result.IsReplay).Should().Be(1, "exactly one caller created it");
        results.Count(result => result.IsReplay).Should().Be(concurrency - 1);
    }

    /// <summary>
    /// The control for the test above, and the justification for the gate existing.
    ///
    /// Remove the gate from a store that ignores unique indexes and the naive
    /// read-then-write races, producing duplicates — exactly the bug the brief
    /// describes. Asserting the broken behaviour keeps the reason for the gate
    /// visible to whoever is tempted to delete it.
    /// </summary>
    [Fact]
    public async Task Without_the_gate_an_unconstrained_store_produces_duplicates()
    {
        var harness = new OrderServiceHarness(
            enforceUniqueIndex: false,
            useRealGate: false,
            writeLatency: TimeSpan.FromMilliseconds(15));

        var request = OrderRequestBuilder.AnOrder().Build();

        await Task.WhenAll(
            Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => harness.Service.SubmitAsync(request, CancellationToken.None))));

        harness.Orders.Count.Should().BeGreaterThan(1,
            "this is the failure mode the gate and the unique index exist to prevent");
    }

    /// <summary>
    /// Multi-instance behaviour, where an in-process gate cannot help.
    ///
    /// The store rejects the insert; the service must treat that as "somebody
    /// else already recorded exactly this order" rather than surfacing a 409.
    /// </summary>
    [Fact]
    public async Task Losing_the_insert_race_resolves_as_a_replay()
    {
        var request = OrderRequestBuilder.AnOrder().Build();
        var service = BuildServiceWithRaceLoss(request, winnerHasSameContent: true, out var repository);

        var result = await service.SubmitAsync(request, CancellationToken.None);

        result.IsReplay.Should().BeTrue();
        result.Order.Id.Should().Be(repository.Winner!.Id);
        repository.AddAttempts.Should().Be(1, "we tried once, lost, and did not retry blindly");
    }

    /// <summary>
    /// Same race, but the order that won is a different order. Recovering from a
    /// lost race must not paper over a genuine conflict.
    /// </summary>
    [Fact]
    public async Task Losing_the_race_to_a_different_order_is_still_a_conflict()
    {
        var request = OrderRequestBuilder.AnOrder().Build();
        var service = BuildServiceWithRaceLoss(request, winnerHasSameContent: false, out _);

        var act = async () => await service.SubmitAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ReferenceReusedException>();
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    public static TheoryData<string, SubmitOrderRequest> InvalidRequests() => new()
    {
        { "missing reference", OrderRequestBuilder.AnOrder().WithReference("").Build() },
        { "bad currency", OrderRequestBuilder.AnOrder().WithCurrency("DOLLARS").Build() },
        { "no lines", OrderRequestBuilder.AnOrder().WithLines().Build() },
        { "zero quantity", OrderRequestBuilder.AnOrder().WithLine("SKU", 0, 10m).Build() },
        { "negative quantity", OrderRequestBuilder.AnOrder().WithLine("SKU", -1, 10m).Build() },
        { "negative price", OrderRequestBuilder.AnOrder().WithLine("SKU", 1, -1m).Build() },
        { "sub-cent price", OrderRequestBuilder.AnOrder().WithLine("SKU", 1, 1.005m).Build() },
        { "bad email", OrderRequestBuilder.AnOrder().ForCustomer("not-an-email").Build() },
        {
            "repeated sku",
            OrderRequestBuilder.AnOrder().WithLines(
                new OrderLineRequest { Sku = "A", Name = "A", Quantity = 1, UnitPrice = 1m },
                new OrderLineRequest { Sku = "a", Name = "A", Quantity = 2, UnitPrice = 1m }).Build()
        }
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_requests_are_rejected_before_anything_is_written(
        string because,
        SubmitOrderRequest request)
    {
        var harness = new OrderServiceHarness();

        var act = async () => await harness.Service.SubmitAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>(because);
        harness.Orders.Count.Should().Be(0, because);
    }

    [Fact]
    public async Task A_rejected_request_reports_every_problem_at_once()
    {
        var harness = new OrderServiceHarness();

        var request = OrderRequestBuilder.AnOrder()
            .WithReference("")
            .WithCurrency("DOLLARS")
            .ForCustomer("nope")
            .WithLine("SKU", 0, -1m)
            .Build();

        var act = async () => await harness.Service.SubmitAsync(request, CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<ValidationException>()).Which;

        // A form should be able to light up every bad field in one pass.
        exception.Errors.Should().HaveCountGreaterThan(3);
        exception.Errors.Select(failure => failure.PropertyName).Should().Contain("ExternalReference");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// A store that behaves as though another process inserted the same reference
    /// in between our read and our write: the first lookup finds nothing, the
    /// insert is rejected, and the second lookup returns the winner.
    ///
    /// Scripted rather than genuinely raced, so the test is deterministic. There
    /// is no value in a test of race recovery that itself fails intermittently.
    /// </summary>
    private sealed class RaceLosingOrderRepository : IOrderRepository
    {
        private readonly Order _winner;
        private int _lookups;
        private int _addAttempts;

        public RaceLosingOrderRepository(Order winner) => _winner = winner;

        public Order? Winner => _winner;

        public int AddAttempts => _addAttempts;

        public Task<Order?> FindByReferenceAsync(
            Guid customerId,
            string externalReference,
            CancellationToken cancellationToken) =>
            Task.FromResult(Interlocked.Increment(ref _lookups) == 1 ? null : _winner);

        public Task AddAsync(Order order, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _addAttempts);
            throw new DuplicateReferenceException(order.ExternalReference);
        }

        public Task<Order?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<Order?>(_winner);

        public Task UpdateAsync(Order order, CancellationToken cancellationToken) => Task.CompletedTask;

        // Reference issuance plays no part in a race on submission — the
        // reference is already in the request by the time we get here.
        public Task<IReadOnlyList<string>> ListReferencesStartingWithAsync(
            string prefix,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PagedResult<OrderSummary>> ListAsync(
            ListOrdersQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static OrderService BuildServiceWithRaceLoss(
        SubmitOrderRequest request,
        bool winnerHasSameContent,
        out RaceLosingOrderRepository repository)
    {
        var customers = new FakeCustomerRepository();

        var customer = customers
            .GetOrCreateAsync(request.Customer.Email, request.Customer.Name, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var winnerRequest = winnerHasSameContent
            ? request
            : OrderRequestBuilder.AnOrder()
                .WithReference(request.ExternalReference)
                .WithLine("KB-001", 999, 1m)
                .Build();

        var winner = Order.Create(
            customer.Id,
            request.ExternalReference,
            winnerRequest.Currency,
            winnerRequest.Notes,
            winnerRequest.Lines
                .Select(line => new OrderLineDraft(line.Sku, line.Name, line.Quantity, line.UnitPrice))
                .ToList(),
            RequestFingerprint.Compute(winnerRequest),
            DateTimeOffset.UtcNow);

        repository = new RaceLosingOrderRepository(winner);

        return new OrderService(
            repository,
            customers,
            new NoOpIdempotencyGate(),
            new FixedClock(),
            new SubmitOrderRequestValidator(),
            NullLogger<OrderService>.Instance);
    }
}
