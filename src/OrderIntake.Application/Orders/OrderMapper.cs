using OrderIntake.Application.Orders.Contracts;
using OrderIntake.Domain.Customers;
using OrderIntake.Domain.Orders;

namespace OrderIntake.Application.Orders;

/// <summary>
/// Hand-written mapping from domain objects to response contracts.
///
/// No AutoMapper here on purpose: for this many types a convention-based mapper
/// costs more than it saves, and a mis-mapped total is the kind of bug that is
/// much easier to spot in explicit code than in configuration.
/// </summary>
internal static class OrderMapper
{
    public static OrderResponse ToResponse(Order order, Customer customer) => new()
    {
        Id = order.Id,
        ExternalReference = order.ExternalReference,
        Customer = new CustomerResponse
        {
            Id = customer.Id,
            Email = customer.Email,
            Name = customer.Name
        },
        Status = order.Status.ToString(),
        Currency = order.Currency,
        Notes = order.Notes,
        Subtotal = order.Subtotal,
        Total = order.Total,
        CreatedAtUtc = order.CreatedAtUtc,
        UpdatedAtUtc = order.UpdatedAtUtc,
        Lines = order.Lines
            .Select(line => new OrderLineResponse
            {
                Id = line.Id,
                Sku = line.Sku,
                Name = line.Name,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                LineTotal = line.LineTotal
            })
            .ToList(),
        AllowedTransitions = order.AllowedTransitions
            .Select(status => status.ToString())
            .ToList()
    };
}
