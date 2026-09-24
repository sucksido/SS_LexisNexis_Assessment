using OrderIntake.Domain.Common;

namespace OrderIntake.Domain.Orders;

/// <summary>
/// A line on an order. Part of the Order aggregate: it has no identity or
/// lifecycle outside its parent, which is why the constructor is internal and
/// lines can only be created through <see cref="Order.Create"/>.
/// </summary>
public sealed class OrderLine
{
    // Required by EF Core's materialiser.
    private OrderLine()
    {
    }

    internal OrderLine(string sku, string name, int quantity, decimal unitPrice)
    {
        Sku = Require(sku, nameof(sku));
        Name = Require(name, nameof(name));

        if (quantity <= 0)
        {
            throw new DomainValidationException(
                $"Quantity for SKU '{Sku}' must be a positive whole number, but was {quantity}.");
        }

        if (unitPrice < 0)
        {
            throw new DomainValidationException(
                $"Unit price for SKU '{Sku}' must not be negative, but was {unitPrice}.");
        }

        // Prices are quoted in whole cents, and that is a domain rule rather than
        // merely an input-format rule — so it is enforced here as well as in the
        // request validator, for the same reason every other invariant is.
        //
        // The temptation is to round instead of reject. That is worse than it
        // looks: rounding the *unit* price and then multiplying turns three units
        // of 9.995 into 30.00 rather than 29.99, and the error grows with the
        // quantity. A price we were not asked to change is not ours to change.
        if (!Money.IsCentPrecision(unitPrice))
        {
            throw new DomainValidationException(
                $"Unit price for SKU '{Sku}' cannot have more than {Money.Scale} decimal places, " +
                $"but was {unitPrice}.");
        }

        Id = Guid.NewGuid();
        Quantity = quantity;
        UnitPrice = unitPrice;

        // Computed on the server, never trusted from the client.
        //
        // Given the guard above, this product is already exact to the cent and the
        // rounding call cannot currently change anything. It stays because the
        // first percentage discount, tax rate or currency conversion to land on
        // this line will make it load-bearing, and because the alternative is
        // rediscovering the rounding-mode question under time pressure.
        LineTotal = Money.Round(unitPrice * quantity);
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal LineTotal { get; private set; }

    private static string Require(string? value, string field)
    {
        var trimmed = (value ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new DomainValidationException($"Order line '{field}' is required.");
        }

        return trimmed;
    }
}

/// <summary>
/// The shape the application layer hands to <see cref="Order.Create"/>.
/// Keeps the aggregate free of any dependency on transport-level DTOs.
/// </summary>
public sealed record OrderLineDraft(string Sku, string Name, int Quantity, decimal UnitPrice);
