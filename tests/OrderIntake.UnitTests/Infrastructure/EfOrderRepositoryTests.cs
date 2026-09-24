using Microsoft.EntityFrameworkCore;
using OrderIntake.Application.Orders.Contracts;
using OrderIntake.Domain.Customers;
using OrderIntake.Domain.Orders;
using OrderIntake.Infrastructure.Persistence;

namespace OrderIntake.UnitTests.Infrastructure;

/// <summary>
/// Exercises the real EF query rather than a hand-written fake.
///
/// Ordering, filtering and paging are expressed in LINQ that a provider has to
/// translate, so testing them against a substitute would only prove that the
/// substitute sorts correctly.
/// </summary>
public sealed class EfOrderRepositoryTests : IAsyncLifetime
{
    private readonly OrderIntakeDbContext _db;
    private readonly EfOrderRepository _repository;
    private readonly Customer _ada = Customer.Create("ada@contoso.com", "Ada Lovelace");
    private readonly Customer _grace = Customer.Create("grace@contoso.com", "Grace Hopper");

    public EfOrderRepositoryTests()
    {
        // A fresh database per test class: no shared state, no ordering coupling
        // between tests.
        var options = new DbContextOptionsBuilder<OrderIntakeDbContext>()
            .UseInMemoryDatabase($"orders-{Guid.NewGuid()}")
            .Options;

        _db = new OrderIntakeDbContext(options);
        _repository = new EfOrderRepository(_db);
    }

    public async Task InitializeAsync()
    {
        _db.Customers.AddRange(_ada, _grace);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<Order> Seed(
        Customer customer,
        string reference,
        DateTimeOffset createdAt,
        OrderStatus status = OrderStatus.Pending,
        decimal unitPrice = 10m,
        int lineCount = 1)
    {
        var lines = Enumerable.Range(1, lineCount)
            .Select(i => new OrderLineDraft($"SKU-{reference}-{i}", $"Item {i}", 1, unitPrice))
            .ToList();

        var order = Order.Create(customer.Id, reference, "USD", null, lines, $"fp-{reference}", createdAt);

        if (status != OrderStatus.Pending)
        {
            if (status is OrderStatus.Fulfilled)
            {
                order.ChangeStatus(OrderStatus.Confirmed, createdAt);
            }

            order.ChangeStatus(status, createdAt);
        }

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        return order;
    }

    private static ListOrdersQuery Query(
        int page = 1,
        int pageSize = 20,
        OrderStatus? status = null,
        string? search = null) =>
        new() { Page = page, PageSize = pageSize, Status = status, Search = search };

    private static readonly DateTimeOffset Monday = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Orders_are_listed_newest_first()
    {
        await Seed(_ada, "PO-OLD", Monday);
        await Seed(_ada, "PO-MID", Monday.AddDays(1));
        await Seed(_ada, "PO-NEW", Monday.AddDays(2));

        var result = await _repository.ListAsync(Query(), CancellationToken.None);

        result.Items.Select(order => order.ExternalReference)
            .Should().ContainInOrder("PO-NEW", "PO-MID", "PO-OLD");
    }

    [Fact]
    public async Task The_summary_carries_what_the_list_screen_needs()
    {
        await Seed(_ada, "PO-1", Monday, OrderStatus.Confirmed, unitPrice: 12.50m, lineCount: 3);

        var summary = (await _repository.ListAsync(Query(), CancellationToken.None)).Items.Single();

        summary.ExternalReference.Should().Be("PO-1");
        summary.CustomerName.Should().Be("Ada Lovelace");
        summary.CustomerEmail.Should().Be("ada@contoso.com");
        summary.Status.Should().Be("Confirmed");
        summary.Currency.Should().Be("USD");
        summary.Total.Should().Be(37.50m);
        summary.LineCount.Should().Be(3);
    }

    [Fact]
    public async Task Listing_can_be_filtered_by_status()
    {
        await Seed(_ada, "PO-P", Monday);
        await Seed(_ada, "PO-C", Monday.AddMinutes(1), OrderStatus.Confirmed);
        await Seed(_ada, "PO-X", Monday.AddMinutes(2), OrderStatus.Cancelled);

        var confirmed = await _repository.ListAsync(
            Query(status: OrderStatus.Confirmed), CancellationToken.None);

        confirmed.TotalCount.Should().Be(1);
        confirmed.Items.Single().ExternalReference.Should().Be("PO-C");
    }

    [Theory]
    [InlineData("po-1", "PO-1")]          // reference, different casing
    [InlineData("GRACE", "PO-2")]         // customer email
    [InlineData("hopper", "PO-2")]        // customer name
    public async Task Search_matches_reference_name_and_email(string term, string expectedReference)
    {
        await Seed(_ada, "PO-1", Monday);
        await Seed(_grace, "PO-2", Monday.AddMinutes(1));

        var result = await _repository.ListAsync(Query(search: term), CancellationToken.None);

        result.Items.Should().ContainSingle()
            .Which.ExternalReference.Should().Be(expectedReference);
    }

    [Fact]
    public async Task Paging_reports_the_full_count_not_just_the_page()
    {
        for (var i = 0; i < 25; i++)
        {
            await Seed(_ada, $"PO-{i:D3}", Monday.AddMinutes(i));
        }

        var firstPage = await _repository.ListAsync(Query(page: 1, pageSize: 10), CancellationToken.None);

        firstPage.Items.Should().HaveCount(10);
        firstPage.TotalCount.Should().Be(25);
        firstPage.TotalPages.Should().Be(3);
        firstPage.HasNextPage.Should().BeTrue();

        var lastPage = await _repository.ListAsync(Query(page: 3, pageSize: 10), CancellationToken.None);

        lastPage.Items.Should().HaveCount(5);
        lastPage.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public async Task Page_size_is_capped_so_a_client_cannot_ask_for_everything()
    {
        var query = new ListOrdersQuery { PageSize = 10_000 };

        query.PageSize.Should().Be(ListOrdersQuery.MaxPageSize);

        var result = await _repository.ListAsync(query, CancellationToken.None);
        result.PageSize.Should().Be(ListOrdersQuery.MaxPageSize);
    }

    [Fact]
    public async Task A_lookup_by_reference_is_scoped_to_the_customer()
    {
        await Seed(_ada, "PO-SHARED", Monday);
        await Seed(_grace, "PO-SHARED", Monday.AddMinutes(1));

        var adasOrder = await _repository.FindByReferenceAsync(_ada.Id, "PO-SHARED", CancellationToken.None);
        var gracesOrder = await _repository.FindByReferenceAsync(_grace.Id, "PO-SHARED", CancellationToken.None);

        adasOrder.Should().NotBeNull();
        gracesOrder.Should().NotBeNull();
        adasOrder!.Id.Should().NotBe(gracesOrder!.Id);
    }

    [Fact]
    public async Task Loading_an_order_brings_its_lines_with_it()
    {
        var seeded = await Seed(_ada, "PO-1", Monday, lineCount: 4);
        _db.ChangeTracker.Clear();

        var loaded = await _repository.GetByIdAsync(seeded.Id, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Lines.Should().HaveCount(4, "the Lines navigation is configured with AutoInclude");
    }

    /// <summary>
    /// The in-memory provider accepts a unique index and then ignores it, so no
    /// runtime test can prove the constraint exists. This asserts it is declared
    /// on the model, which is what a real provider would enforce — and it fails
    /// the day someone removes it from the configuration.
    /// </summary>
    [Fact]
    public void The_unique_index_on_customer_and_reference_is_declared()
    {
        var entity = _db.Model.FindEntityType(typeof(Order));
        entity.Should().NotBeNull();

        var uniqueIndexes = entity!.GetIndexes()
            .Where(index => index.IsUnique)
            .Select(index => index.Properties.Select(property => property.Name).ToArray())
            .ToList();

        uniqueIndexes.Should().ContainEquivalentOf(
            new[] { nameof(Order.CustomerId), nameof(Order.ExternalReference) });
    }

    [Fact]
    public void Customer_email_is_declared_unique()
    {
        var entity = _db.Model.FindEntityType(typeof(Customer));

        entity!.GetIndexes()
            .Where(index => index.IsUnique)
            .SelectMany(index => index.Properties.Select(property => property.Name))
            .Should().Contain(nameof(Customer.Email));
    }

    /// <summary>
    /// Persisting the status as text means a future enum member inserted in the
    /// middle cannot silently reinterpret existing rows.
    /// </summary>
    [Fact]
    public void Status_is_persisted_as_text()
    {
        var property = _db.Model.FindEntityType(typeof(Order))!.FindProperty(nameof(Order.Status));

        property!.GetProviderClrType().Should().Be<string>();
    }
}
