using OrderIntake.Application.Orders.Contracts;

namespace OrderIntake.UnitTests.Support;

/// <summary>
/// Builds submission requests for tests.
///
/// Every test starts from the same valid request and changes only the one thing
/// it cares about, so the assertion and the cause sit next to each other instead
/// of being buried in twenty lines of object initialiser.
/// </summary>
public sealed class OrderRequestBuilder
{
    private string _reference = "PO-1001";
    private string _currency = "USD";
    private string? _notes;
    private string _email = "sam@contoso.com";
    private string _customerName = "Sam Carter";
    private List<OrderLineRequest> _lines = new()
    {
        new OrderLineRequest { Sku = "KB-001", Name = "Mechanical Keyboard", Quantity = 2, UnitPrice = 89.99m },
        new OrderLineRequest { Sku = "MS-014", Name = "Wireless Mouse", Quantity = 3, UnitPrice = 24.50m }
    };

    public static OrderRequestBuilder AnOrder() => new();

    public OrderRequestBuilder WithReference(string reference)
    {
        _reference = reference;
        return this;
    }

    public OrderRequestBuilder WithCurrency(string currency)
    {
        _currency = currency;
        return this;
    }

    public OrderRequestBuilder WithNotes(string? notes)
    {
        _notes = notes;
        return this;
    }

    public OrderRequestBuilder ForCustomer(string email, string? name = null)
    {
        _email = email;
        _customerName = name ?? _customerName;
        return this;
    }

    public OrderRequestBuilder WithLines(params OrderLineRequest[] lines)
    {
        _lines = lines.ToList();
        return this;
    }

    public OrderRequestBuilder WithLine(string sku, int quantity, decimal unitPrice, string? name = null)
    {
        _lines = new List<OrderLineRequest>
        {
            new() { Sku = sku, Name = name ?? sku, Quantity = quantity, UnitPrice = unitPrice }
        };

        return this;
    }

    /// <summary>Same basket, entered in a different order. Must fingerprint identically.</summary>
    public OrderRequestBuilder WithLinesReversed()
    {
        _lines = Enumerable.Reverse(_lines).ToList();
        return this;
    }

    public SubmitOrderRequest Build() => new()
    {
        ExternalReference = _reference,
        Currency = _currency,
        Notes = _notes,
        Customer = new CustomerRequest { Email = _email, Name = _customerName },
        Lines = _lines.ToList()
    };
}
