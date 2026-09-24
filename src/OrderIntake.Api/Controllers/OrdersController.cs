using Microsoft.AspNetCore.Mvc;
using OrderIntake.Application.Abstractions;
using OrderIntake.Application.Orders;
using OrderIntake.Application.Orders.Contracts;
using OrderIntake.Domain.Orders;

namespace OrderIntake.Api.Controllers;

/// <summary>
/// Purchase order intake and tracking.
/// </summary>
[ApiController]
[Route("api/orders")]
[Produces("application/json")]
public sealed class OrdersController : ControllerBase
{
    /// <summary>
    /// Response header set when a submission matched an order that already
    /// existed. The status code carries the same information; the header is
    /// there so a client can tell the two apart without inspecting the body.
    /// </summary>
    public const string ReplayHeader = "X-Idempotent-Replay";

    private readonly IOrderService _orders;
    private readonly IOrderReferenceSequence _references;

    public OrdersController(IOrderService orders, IOrderReferenceSequence references)
    {
        _orders = orders;
        _references = references;
    }

    /// <summary>
    /// Submits a purchase order.
    /// </summary>
    /// <remarks>
    /// Idempotent on <c>externalReference</c>, scoped to the customer.
    ///
    /// Returns <c>201 Created</c> for a new order and <c>200 OK</c> when the same
    /// reference and content have already been recorded — so a rep who clicks
    /// Submit twice gets a success both times and exactly one order exists.
    ///
    /// Re-using a reference with different line items or currency is a
    /// <c>409 Conflict</c>: that is a different order, not a repeat, and pretending
    /// otherwise would tell the rep their amendment was saved when it was not.
    /// </remarks>
    /// <response code="201">A new order was created.</response>
    /// <response code="200">This submission matched an existing order.</response>
    /// <response code="400">The request failed validation.</response>
    /// <response code="409">The reference is already used by a different order.</response>
    [HttpPost]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderResponse>> Submit(
        [FromBody] SubmitOrderRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _orders.SubmitAsync(request, cancellationToken);

        Response.Headers[ReplayHeader] = result.IsReplay ? "true" : "false";

        if (result.IsReplay)
        {
            return Ok(result.Order);
        }

        return CreatedAtAction(
            nameof(GetById),
            new { orderId = result.Order.Id },
            result.Order);
    }

    /// <summary>
    /// Issues the next order reference for a new submission.
    /// </summary>
    /// <remarks>
    /// The intake form calls this when it opens and shows the result read-only,
    /// so the rep never invents a reference and never re-types one.
    ///
    /// Read this as a suggestion, not a reservation. Nothing is written here and
    /// nothing is held: a form that is opened and abandoned simply leaves a gap,
    /// and on more than one API instance two callers can be given the same
    /// number. Uniqueness is still enforced where it always was — the unique
    /// index on (customer, reference) — and a caller that loses that race gets
    /// the same 409 it would have got by typing the number by hand.
    /// </remarks>
    /// <response code="200">The reference to display on a new order form.</response>
    [HttpGet("next-reference")]
    [ProducesResponseType(typeof(NextReferenceResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<NextReferenceResponse>> GetNextReference(
        CancellationToken cancellationToken) =>
        Ok(new NextReferenceResponse(await _references.NextAsync(cancellationToken)));

    /// <summary>Lists orders, newest first.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Rows per page (capped at 100).</param>
    /// <param name="status">Optional status filter, e.g. <c>Pending</c>.</param>
    /// <param name="search">Optional free-text match on reference, customer name or email.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Filtering and paging run in the store rather than in the browser. Client-side
    /// filtering would be simpler and would quietly stop being correct the moment
    /// the result set outgrows one page.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<OrderSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<OrderSummary>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] OrderStatus? status = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var query = new ListOrdersQuery
        {
            Page = page,
            PageSize = pageSize,
            Status = status,
            Search = search
        };

        return Ok(await _orders.ListAsync(query, cancellationToken));
    }

    /// <summary>Retrieves a single order with its line items and totals.</summary>
    [HttpGet("{orderId:guid}", Name = nameof(GetById))]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderResponse>> GetById(
        Guid orderId,
        CancellationToken cancellationToken) =>
        Ok(await _orders.GetAsync(orderId, cancellationToken));

    /// <summary>
    /// Changes an order's status.
    /// </summary>
    /// <remarks>
    /// Legal moves: Pending to Confirmed or Cancelled; Confirmed to Fulfilled or
    /// Cancelled. Fulfilled and Cancelled are final.
    ///
    /// Asking for the status the order already has succeeds without writing
    /// anything — the same reasoning that makes submission idempotent. Anything
    /// the policy forbids returns 409 with the allowed alternatives attached.
    ///
    /// Modelled as PUT on a status sub-resource rather than PATCH on the order:
    /// this is a specific state transition with its own rules, not a free-form
    /// field edit, and the URL should say so.
    /// </remarks>
    /// <response code="200">The order is now in the requested status.</response>
    /// <response code="404">No such order.</response>
    /// <response code="409">The transition is not permitted from the current status.</response>
    [HttpPut("{orderId:guid}/status")]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderResponse>> ChangeStatus(
        Guid orderId,
        [FromBody] ChangeOrderStatusRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _orders.ChangeStatusAsync(orderId, request.Status, cancellationToken);

        Response.Headers["X-Status-Changed"] = result.Changed ? "true" : "false";

        return Ok(result.Order);
    }

    /// <summary>
    /// Returns the status graph.
    /// </summary>
    /// <remarks>
    /// Lets the UI describe the workflow without hard-coding it. The transition
    /// rules live in the domain; anything that needs to know them asks.
    /// </remarks>
    [HttpGet("statuses")]
    [ProducesResponseType(typeof(IReadOnlyList<StatusDescriptor>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<StatusDescriptor>> GetStatuses() =>
        Ok(Enum.GetValues<OrderStatus>()
            .Select(status => new StatusDescriptor(
                status.ToString(),
                OrderStatusPolicy.AllowedTransitionsFrom(status).Select(next => next.ToString()).ToArray(),
                OrderStatusPolicy.IsTerminal(status)))
            .ToArray());
}

/// <summary>One node of the order status graph.</summary>
/// <param name="Status">The status name.</param>
/// <param name="AllowedTransitions">Statuses reachable from here.</param>
/// <param name="IsTerminal">True when no further change is possible.</param>
public sealed record StatusDescriptor(
    string Status,
    IReadOnlyList<string> AllowedTransitions,
    bool IsTerminal);

/// <summary>A reference for the intake form to display.</summary>
/// <param name="ExternalReference">The suggested reference, e.g. <c>PO-000042</c>.</param>
public sealed record NextReferenceResponse(string ExternalReference);
