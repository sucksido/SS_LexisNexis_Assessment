using OrderIntake.Domain.Common;
using OrderIntake.Domain.Orders;

namespace OrderIntake.UnitTests.Domain;

/// <summary>
/// Totals are computed on the server and never accepted from the client, so
/// these tests are the only thing standing between a pricing bug and an invoice.
/// </summary>
public sealed class OrderTotalsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.NewGuid();

    private static Order Create(params OrderLineDraft[] lines) =>
        Order.Create(CustomerId, "PO-1001", "USD", null, lines, "fingerprint", Now);

    [Fact]
    public void Line_total_is_quantity_times_unit_price()
    {
        var order = Create(new OrderLineDraft("KB-001", "Keyboard", 3, 19.99m));

        order.Lines.Single().LineTotal.Should().Be(59.97m);
    }

    [Fact]
    public void Subtotal_and_total_are_the_sum_of_line_totals()
    {
        var order = Create(
            new OrderLineDraft("KB-001", "Keyboard", 2, 89.99m),
            new OrderLineDraft("MS-014", "Mouse", 3, 24.50m));

        // 179.98 + 73.50
        order.Subtotal.Should().Be(253.48m);
        order.Total.Should().Be(order.Subtotal);
    }

    [Fact]
    public void Free_lines_are_allowed_and_contribute_nothing()
    {
        var order = Create(
            new OrderLineDraft("KB-001", "Keyboard", 1, 100m),
            new OrderLineDraft("GIFT-01", "Promotional sticker", 5, 0m));

        order.Total.Should().Be(100m);
    }

    /// <summary>
    /// A price finer than a cent is refused, not quietly rounded.
    ///
    /// Rounding it would be the friendlier-looking choice and the wrong one:
    /// rounding 9.995 up to 10.00 and then multiplying by a quantity of three
    /// bills 30.00 for something the rep priced at 29.99, and the discrepancy
    /// grows with the order. The rounding behaviour itself is pinned in
    /// MoneyTests; this is about what the aggregate refuses to accept.
    /// </summary>
    // Attribute arguments cannot be decimal literals in C#, so the values arrive
    // as double and are converted. Each is exact under that conversion.
    [Theory]
    [InlineData(2.345)]
    [InlineData(0.125)]
    [InlineData(1.115)]
    [InlineData(19.9999)]
    public void A_price_finer_than_a_cent_is_refused_rather_than_rounded(double unitPrice)
    {
        var act = () => Create(new OrderLineDraft("SKU", "Item", 1, (decimal)unitPrice));

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*more than 2 decimal places*");
    }

    /// <summary>
    /// The property a person checking the order by hand depends on: every row
    /// multiplies out exactly, and the rows add up to the figure at the bottom
    /// with no rounding drift tucked in between. Cent-precision prices and whole
    /// quantities are what make that guarantee available at all.
    /// </summary>
    [Fact]
    public void The_subtotal_is_exactly_the_sum_of_the_line_totals()
    {
        var order = Create(
            new OrderLineDraft("A", "Penny part", 3, 0.01m),
            new OrderLineDraft("B", "Odd price", 7, 1.11m),
            new OrderLineDraft("C", "Nearly ten", 11, 9.99m));

        order.Lines.Select(line => line.LineTotal).Should().Equal(0.03m, 7.77m, 109.89m);
        order.Subtotal.Should().Be(117.69m);
        order.Subtotal.Should().Be(order.Lines.Sum(line => line.LineTotal));
    }

    /// <summary>
    /// The price the rep typed is the price that is stored. Nothing in the
    /// aggregate is allowed to adjust it on the way in.
    /// </summary>
    [Fact]
    public void The_submitted_unit_price_is_stored_untouched()
    {
        var order = Create(new OrderLineDraft("SKU", "Item", 4, 9.95m));

        order.Lines.Single().UnitPrice.Should().Be(9.95m);
        order.Lines.Single().LineTotal.Should().Be(39.80m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Quantity_must_be_positive(int quantity)
    {
        var act = () => Create(new OrderLineDraft("KB-001", "Keyboard", quantity, 10m));

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*positive whole number*");
    }

    [Fact]
    public void Unit_price_must_not_be_negative()
    {
        var act = () => Create(new OrderLineDraft("KB-001", "Keyboard", 1, -0.01m));

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*must not be negative*");
    }

    [Fact]
    public void An_order_needs_at_least_one_line()
    {
        var act = () => Create();

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*at least one line item*");
    }

    /// <summary>
    /// Two lines for the same SKU are almost always a mis-click, and they make
    /// the fingerprint ambiguous. Reject rather than guess.
    /// </summary>
    [Fact]
    public void The_same_sku_cannot_appear_twice()
    {
        var act = () => Create(
            new OrderLineDraft("KB-001", "Keyboard", 1, 10m),
            new OrderLineDraft("kb-001", "Keyboard again", 2, 10m));

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*more than one line*");
    }

    [Theory]
    [InlineData("US")]
    [InlineData("DOLLARS")]
    [InlineData("")]
    [InlineData("12A")]
    public void Currency_must_be_an_iso_code(string currency)
    {
        var act = () => Order.Create(
            CustomerId, "PO-1", currency, null,
            new[] { new OrderLineDraft("SKU", "Item", 1, 1m) }, "fp", Now);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*ISO 4217*");
    }

    [Fact]
    public void Currency_and_reference_are_normalised()
    {
        var order = Order.Create(
            CustomerId, "  PO-1001  ", "usd", "  ", // whitespace-only notes become null
            new[] { new OrderLineDraft(" KB-001 ", " Keyboard ", 1, 1m) }, "fp", Now);

        order.Currency.Should().Be("USD");
        order.ExternalReference.Should().Be("PO-1001");
        order.Notes.Should().BeNull();
        order.Lines.Single().Sku.Should().Be("KB-001");
        order.Lines.Single().Name.Should().Be("Keyboard");
    }

    [Fact]
    public void A_reference_is_required()
    {
        var act = () => Order.Create(
            CustomerId, "   ", "USD", null,
            new[] { new OrderLineDraft("SKU", "Item", 1, 1m) }, "fp", Now);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*external reference is required*");
    }

    [Fact]
    public void A_new_order_starts_pending_and_is_timestamped()
    {
        var order = Create(new OrderLineDraft("SKU", "Item", 1, 1m));

        order.Status.Should().Be(OrderStatus.Pending);
        order.CreatedAtUtc.Should().Be(Now);
        order.UpdatedAtUtc.Should().Be(Now);
    }
}
