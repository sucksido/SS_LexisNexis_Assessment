using FluentValidation;
using Microsoft.Extensions.Logging;
using OrderIntake.Application.Abstractions;
using OrderIntake.Application.Common;
using OrderIntake.Application.Orders.Contracts;
using OrderIntake.Domain.Customers;
using OrderIntake.Domain.Orders;

namespace OrderIntake.Application.Orders;

/// <summary>
/// The use-case layer for orders.
///
/// The interesting part of this class is <see cref="SubmitAsync"/>. Everything
/// else is plumbing.
/// </summary>
public sealed class OrderService : IOrderService
{
    private readonly IOrderRepository _orders;
    private readonly ICustomerRepository _customers;
    private readonly IIdempotencyGate _gate;
    private readonly IClock _clock;
    private readonly IValidator<SubmitOrderRequest> _validator;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        IOrderRepository orders,
        ICustomerRepository customers,
        IIdempotencyGate gate,
        IClock clock,
        IValidator<SubmitOrderRequest> validator,
        ILogger<OrderService> logger)
    {
        _orders = orders;
        _customers = customers;
        _gate = gate;
        _clock = clock;
        _validator = validator;
        _logger = logger;
    }

    /// <summary>
    /// Submits an order idempotently.
    ///
    /// The problem being solved: a rep clicks Submit, the response is slow, they
    /// click again. Two identical requests are now in flight. We must end up with
    /// exactly one order, and both requests must get a sensible answer.
    ///
    /// Three defences, deliberately layered, because each one alone has a hole:
    ///
    ///   1. A read-before-write check. Cheap and handles the common case, where
    ///      the second click arrives after the first has finished. On its own it
    ///      is a classic check-then-act race.
    ///
    ///   2. An in-process gate keyed on (customer, reference). Closes that race
    ///      for requests hitting the same instance. On its own it does nothing
    ///      across a load-balanced pair of instances.
    ///
    ///   3. A unique index in the store, surfaced as DuplicateReferenceException
    ///      and handled below. This is the only defence that actually holds under
    ///      multiple processes — the other two are latency optimisations sitting
    ///      in front of it.
    ///
    /// The layering is the point. See SOLUTION.md for why the in-memory provider
    /// makes defence 2 load-bearing in the default configuration.
    /// </summary>
    public async Task<SubmitOrderResult> SubmitAsync(
        SubmitOrderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Throws ValidationException, which the API turns into a 400 listing every
        // field problem at once rather than the first one it trips over.
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var reference = request.ExternalReference.Trim();
        var fingerprint = RequestFingerprint.Compute(request);

        var customer = await _customers.GetOrCreateAsync(
            request.Customer.Email,
            request.Customer.Name,
            cancellationToken);

        var gateKey = BuildGateKey(customer.Id, reference);

        await using (await _gate.AcquireAsync(gateKey, cancellationToken))
        {
            var existing = await _orders.FindByReferenceAsync(customer.Id, reference, cancellationToken);

            if (existing is not null)
            {
                return ResolveExisting(existing, customer, fingerprint, reference);
            }

            var order = Order.Create(
                customer.Id,
                reference,
                request.Currency,
                request.Notes,
                request.Lines
                    .Select(line => new OrderLineDraft(line.Sku, line.Name, line.Quantity, line.UnitPrice))
                    .ToList(),
                fingerprint,
                _clock.UtcNow);

            try
            {
                await _orders.AddAsync(order, cancellationToken);
            }
            catch (DuplicateReferenceException)
            {
                // Defence 3 fired: another process inserted the same reference
                // between our read and our write. Not an error — it means the
                // order the caller wanted now exists. Re-read and treat it as a
                // replay, which is exactly what the caller would have got had
                // they arrived a few milliseconds later.
                _logger.LogInformation(
                    "Lost the insert race for reference {Reference} (customer {CustomerId}); resolving as replay.",
                    reference,
                    customer.Id);

                var winner = await _orders.FindByReferenceAsync(customer.Id, reference, cancellationToken)
                             ?? throw new InvalidOperationException(
                                 $"Reference '{reference}' was reported as duplicate but could not be read back. " +
                                 "This indicates the store violated read-your-writes consistency.");

                return ResolveExisting(winner, customer, fingerprint, reference);
            }

            _logger.LogInformation(
                "Created order {OrderId} for reference {Reference} with total {Total} {Currency}.",
                order.Id,
                reference,
                order.Total,
                order.Currency);

            return new SubmitOrderResult
            {
                Order = OrderMapper.ToResponse(order, customer),
                IsReplay = false
            };
        }
    }

    public async Task<OrderResponse> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _orders.GetByIdAsync(orderId, cancellationToken)
                    ?? throw new OrderNotFoundException(orderId);

        var customer = await _customers.GetByIdAsync(order.CustomerId, cancellationToken)
                       ?? throw new InvalidOperationException(
                           $"Order {orderId} references customer {order.CustomerId}, which does not exist.");

        return OrderMapper.ToResponse(order, customer);
    }

    public Task<PagedResult<OrderSummary>> ListAsync(
        ListOrdersQuery query,
        CancellationToken cancellationToken) =>
        _orders.ListAsync(query, cancellationToken);

    public async Task<ChangeOrderStatusResult> ChangeStatusAsync(
        Guid orderId,
        OrderStatus targetStatus,
        CancellationToken cancellationToken)
    {
        var order = await _orders.GetByIdAsync(orderId, cancellationToken)
                    ?? throw new OrderNotFoundException(orderId);

        var customer = await _customers.GetByIdAsync(order.CustomerId, cancellationToken)
                       ?? throw new InvalidOperationException(
                           $"Order {orderId} references customer {order.CustomerId}, which does not exist.");

        var previous = order.Status;

        // Throws InvalidStatusTransitionException, which carries the allowed
        // alternatives so the 409 can tell the user what they *can* do.
        var outcome = order.ChangeStatus(targetStatus, _clock.UtcNow);

        if (outcome == StatusChangeOutcome.Changed)
        {
            await _orders.UpdateAsync(order, cancellationToken);

            _logger.LogInformation(
                "Order {OrderId} moved from {PreviousStatus} to {NewStatus}.",
                orderId,
                previous,
                targetStatus);
        }

        return new ChangeOrderStatusResult
        {
            Order = OrderMapper.ToResponse(order, customer),
            Changed = outcome == StatusChangeOutcome.Changed
        };
    }

    /// <summary>
    /// Decides what a repeat submission means.
    ///
    /// Same reference + same material content = the same order arriving twice.
    /// Return the original. The caller gets 200 instead of 201 and is none the
    /// wiser, which is the behaviour the brief asks for.
    ///
    /// Same reference + different content = two different orders competing for
    /// one reference. Returning the original here would be actively harmful: the
    /// rep would see a success and believe their amended quantities were saved.
    /// </summary>
    private SubmitOrderResult ResolveExisting(
        Order existing,
        Customer customer,
        string fingerprint,
        string reference)
    {
        if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Reference {Reference} was re-submitted with different content (existing order {OrderId}).",
                reference,
                existing.Id);

            throw new ReferenceReusedException(reference, existing.Id);
        }

        _logger.LogInformation(
            "Duplicate submission of reference {Reference} matched existing order {OrderId}.",
            reference,
            existing.Id);

        return new SubmitOrderResult
        {
            Order = OrderMapper.ToResponse(existing, customer),
            IsReplay = true
        };
    }

    private static string BuildGateKey(Guid customerId, string reference) =>
        $"order:{customerId:N}:{reference}";
}
