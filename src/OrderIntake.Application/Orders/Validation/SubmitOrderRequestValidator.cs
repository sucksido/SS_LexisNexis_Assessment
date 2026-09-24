using FluentValidation;
using OrderIntake.Application.Orders.Contracts;
using OrderIntake.Domain.Orders;

namespace OrderIntake.Application.Orders.Validation;

/// <summary>
/// Shape and format validation for an incoming submission.
///
/// This layer answers "is this request well-formed?" and produces a 400 listing
/// every problem at once, which is what a form needs. The domain still enforces
/// the same rules independently — belt and braces, because the domain must stay
/// correct even when a future caller bypasses this validator.
/// </summary>
public sealed class SubmitOrderRequestValidator : AbstractValidator<SubmitOrderRequest>
{
    public const int MaxLinesPerOrder = 200;

    public SubmitOrderRequestValidator()
    {
        RuleFor(request => request.ExternalReference)
            .NotEmpty().WithMessage("A reference is required so we can recognise repeat submissions.")
            .MaximumLength(64);

        RuleFor(request => request.Currency)
            .NotEmpty()
            .Matches("^[A-Za-z]{3}$")
            .WithMessage("Currency must be a 3-letter ISO 4217 code, for example USD or ZAR.");

        // The null-forgiving operator is for the compiler's benefit only: the rule
        // is guarded by When(...), and MaximumLength is declared against a
        // non-nullable string. It does not reach the expression tree, so
        // FluentValidation still reports the field as "Notes".
        RuleFor(request => request.Notes!)
            .MaximumLength(1000)
            .When(request => request.Notes is not null);

        RuleFor(request => request.Customer)
            .NotNull()
            .SetValidator(new CustomerRequestValidator());

        RuleFor(request => request.Lines)
            .NotEmpty().WithMessage("An order needs at least one line item.")
            .Must(lines => lines.Count <= MaxLinesPerOrder)
            .WithMessage($"An order cannot have more than {MaxLinesPerOrder} line items.");

        RuleForEach(request => request.Lines).SetValidator(new OrderLineRequestValidator());

        RuleFor(request => request.Lines)
            .Must(HaveDistinctSkus)
            .When(request => request.Lines.Count > 0)
            .WithMessage("Each SKU may only appear once. Combine repeats into a single line.");
    }

    private static bool HaveDistinctSkus(IReadOnlyList<OrderLineRequest> lines) =>
        lines
            .Select(line => (line.Sku ?? string.Empty).Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == lines.Count;
}

public sealed class CustomerRequestValidator : AbstractValidator<CustomerRequest>
{
    public CustomerRequestValidator()
    {
        RuleFor(customer => customer.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(256);

        RuleFor(customer => customer.Name)
            .NotEmpty()
            .MaximumLength(200);
    }
}

public sealed class OrderLineRequestValidator : AbstractValidator<OrderLineRequest>
{
    public OrderLineRequestValidator()
    {
        RuleFor(line => line.Sku)
            .NotEmpty()
            .MaximumLength(64);

        RuleFor(line => line.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(line => line.Quantity)
            .GreaterThan(0).WithMessage("Quantity must be a positive whole number.")
            .LessThanOrEqualTo(1_000_000);

        RuleFor(line => line.UnitPrice)
            .GreaterThanOrEqualTo(0).WithMessage("Unit price cannot be negative.")
            .LessThanOrEqualTo(1_000_000_000m)
            // Guards against a client sending 19.9999 and then being confused when
            // the server bills 20.00. Reject it rather than silently round it.
            //
            // Money.IsCentPrecision rather than a local expression so this rule and
            // the identical one in the Order aggregate cannot drift apart.
            .Must(Money.IsCentPrecision)
            .WithMessage($"Unit price cannot have more than {Money.Scale} decimal places.");
    }
}

public sealed class ChangeOrderStatusRequestValidator : AbstractValidator<ChangeOrderStatusRequest>
{
    public ChangeOrderStatusRequestValidator()
    {
        RuleFor(request => request.Status)
            .IsInEnum()
            .WithMessage("Status must be one of: Pending, Confirmed, Fulfilled, Cancelled.");
    }
}
