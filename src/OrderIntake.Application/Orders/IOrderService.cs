using OrderIntake.Application.Orders.Contracts;
using OrderIntake.Domain.Orders;

namespace OrderIntake.Application.Orders;

public interface IOrderService
{
    /// <summary>
    /// Records a purchase order, or returns the existing one if this reference
    /// has already been submitted with the same content.
    /// </summary>
    Task<SubmitOrderResult> SubmitAsync(SubmitOrderRequest request, CancellationToken cancellationToken);

    Task<OrderResponse> GetAsync(Guid orderId, CancellationToken cancellationToken);

    Task<PagedResult<OrderSummary>> ListAsync(ListOrdersQuery query, CancellationToken cancellationToken);

    Task<ChangeOrderStatusResult> ChangeStatusAsync(
        Guid orderId,
        OrderStatus targetStatus,
        CancellationToken cancellationToken);
}
