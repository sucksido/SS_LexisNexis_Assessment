using OrderIntake.Domain.Common;

namespace OrderIntake.Application.Common;

/// <summary>
/// Errors that belong to the use-case layer rather than to the domain model:
/// they are about orchestration (does this thing exist? is this request a
/// contradiction of an earlier one?) rather than about an invariant of an Order.
/// </summary>
public abstract class ApplicationRuleException : Exception, IHasErrorCode
{
    protected ApplicationRuleException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class OrderNotFoundException : ApplicationRuleException
{
    public OrderNotFoundException(Guid orderId)
        : base("ORDER_NOT_FOUND", $"No order was found with id {orderId}.")
    {
        OrderId = orderId;
    }

    public Guid OrderId { get; }
}

/// <summary>
/// Raised when an external reference that already exists is re-submitted with a
/// materially different payload.
///
/// This is the case that separates "idempotency" from "swallow the second call".
/// Quietly returning the original order would tell the rep their amended order
/// went through when it did not, so we surface it and let them decide.
/// </summary>
public sealed class ReferenceReusedException : ApplicationRuleException
{
    public ReferenceReusedException(string externalReference, Guid existingOrderId)
        : base(
            "REFERENCE_REUSED_WITH_DIFFERENT_CONTENT",
            $"Reference '{externalReference}' already belongs to an order with different line items or " +
            "currency. If this is a correction, cancel the original order or submit it under a new reference.")
    {
        ExternalReference = externalReference;
        ExistingOrderId = existingOrderId;
    }

    public string ExternalReference { get; }
    public Guid ExistingOrderId { get; }
}

/// <summary>
/// Thrown by the persistence layer when a unique index on
/// (CustomerId, ExternalReference) rejects an insert.
///
/// Declared here, in the abstraction layer, so that <c>OrderService</c> can react
/// to a lost race without ever seeing an EF Core <c>DbUpdateException</c>.
/// </summary>
public sealed class DuplicateReferenceException : ApplicationRuleException
{
    public DuplicateReferenceException(string externalReference, Exception? inner = null)
        : base("DUPLICATE_REFERENCE", $"Reference '{externalReference}' is already in use for this customer.")
    {
        ExternalReference = externalReference;
        InnerStoreException = inner;
    }

    public string ExternalReference { get; }
    public Exception? InnerStoreException { get; }
}
