using OrderIntake.Application.Orders;
using OrderIntake.Application.Orders.Contracts;
using OrderIntake.UnitTests.Support;

namespace OrderIntake.UnitTests.Application;

/// <summary>
/// The fingerprint decides whether a repeat reference is "the same order again"
/// or "a different order reusing a reference". Getting it too strict turns
/// harmless re-sends into errors; too loose and a corrected order is silently
/// discarded. These tests pin down exactly where that line sits.
/// </summary>
public sealed class RequestFingerprintTests
{
    [Fact]
    public void Identical_requests_fingerprint_identically()
    {
        var first = OrderRequestBuilder.AnOrder().Build();
        var second = OrderRequestBuilder.AnOrder().Build();

        RequestFingerprint.Compute(first).Should().Be(RequestFingerprint.Compute(second));
    }

    [Fact]
    public void Line_ordering_does_not_matter()
    {
        var asEntered = OrderRequestBuilder.AnOrder().Build();
        var reversed = OrderRequestBuilder.AnOrder().WithLinesReversed().Build();

        RequestFingerprint.Compute(asEntered).Should().Be(RequestFingerprint.Compute(reversed));
    }

    /// <summary>
    /// Notes are cosmetic. A rep who resubmits after fixing a typo in the notes
    /// should get their original order back, not a conflict they cannot resolve.
    /// </summary>
    [Fact]
    public void Notes_are_not_material()
    {
        var withoutNotes = OrderRequestBuilder.AnOrder().WithNotes(null).Build();
        var withNotes = OrderRequestBuilder.AnOrder().WithNotes("Deliver before Friday.").Build();

        RequestFingerprint.Compute(withoutNotes).Should().Be(RequestFingerprint.Compute(withNotes));
    }

    /// <summary>
    /// Same for the product description: the SKU is what identifies the item.
    /// </summary>
    [Fact]
    public void Product_display_names_are_not_material()
    {
        var first = OrderRequestBuilder.AnOrder().WithLine("KB-001", 1, 10m, "Keyboard").Build();
        var second = OrderRequestBuilder.AnOrder().WithLine("KB-001", 1, 10m, "Keyboard (black)").Build();

        RequestFingerprint.Compute(first).Should().Be(RequestFingerprint.Compute(second));
    }

    [Fact]
    public void Trailing_zeroes_on_a_price_do_not_change_the_fingerprint()
    {
        var first = OrderRequestBuilder.AnOrder().WithLine("KB-001", 1, 89.9m).Build();
        var second = OrderRequestBuilder.AnOrder().WithLine("KB-001", 1, 89.90m).Build();

        RequestFingerprint.Compute(first).Should().Be(RequestFingerprint.Compute(second));
    }

    [Fact]
    public void Sku_casing_does_not_change_the_fingerprint()
    {
        var upper = OrderRequestBuilder.AnOrder().WithLine("KB-001", 1, 10m).Build();
        var lower = OrderRequestBuilder.AnOrder().WithLine("kb-001", 1, 10m).Build();

        RequestFingerprint.Compute(upper).Should().Be(RequestFingerprint.Compute(lower));
    }

    [Fact]
    public void A_different_quantity_is_a_different_order()
    {
        var first = OrderRequestBuilder.AnOrder().WithLine("KB-001", 1, 10m).Build();
        var second = OrderRequestBuilder.AnOrder().WithLine("KB-001", 2, 10m).Build();

        RequestFingerprint.Compute(first).Should().NotBe(RequestFingerprint.Compute(second));
    }

    [Fact]
    public void A_different_price_is_a_different_order()
    {
        var first = OrderRequestBuilder.AnOrder().WithLine("KB-001", 1, 10m).Build();
        var second = OrderRequestBuilder.AnOrder().WithLine("KB-001", 1, 10.01m).Build();

        RequestFingerprint.Compute(first).Should().NotBe(RequestFingerprint.Compute(second));
    }

    [Fact]
    public void A_different_currency_is_a_different_order()
    {
        var usd = OrderRequestBuilder.AnOrder().WithCurrency("USD").Build();
        var zar = OrderRequestBuilder.AnOrder().WithCurrency("ZAR").Build();

        RequestFingerprint.Compute(usd).Should().NotBe(RequestFingerprint.Compute(zar));
    }

    [Fact]
    public void Currency_casing_does_not_change_the_fingerprint()
    {
        var upper = OrderRequestBuilder.AnOrder().WithCurrency("USD").Build();
        var lower = OrderRequestBuilder.AnOrder().WithCurrency("usd").Build();

        RequestFingerprint.Compute(upper).Should().Be(RequestFingerprint.Compute(lower));
    }

    [Fact]
    public void An_extra_line_is_a_different_order()
    {
        var oneLine = OrderRequestBuilder.AnOrder().WithLine("KB-001", 1, 10m).Build();

        var twoLines = OrderRequestBuilder.AnOrder()
            .WithLines(
                new OrderLineRequest { Sku = "KB-001", Name = "Keyboard", Quantity = 1, UnitPrice = 10m },
                new OrderLineRequest { Sku = "MS-014", Name = "Mouse", Quantity = 1, UnitPrice = 5m })
            .Build();

        RequestFingerprint.Compute(oneLine).Should().NotBe(RequestFingerprint.Compute(twoLines));
    }

    [Fact]
    public void The_fingerprint_is_a_fixed_length_hex_hash()
    {
        var fingerprint = RequestFingerprint.Compute(OrderRequestBuilder.AnOrder().Build());

        fingerprint.Should().HaveLength(64);
        fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
    }
}
