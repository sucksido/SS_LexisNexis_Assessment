using OrderIntake.Application.Common;
using OrderIntake.Domain.Orders;
using OrderIntake.UnitTests.Support;

namespace OrderIntake.UnitTests.Application;

public sealed class OrderStatusChangeTests
{
    private static async Task<(OrderServiceHarness Harness, Guid OrderId)> WithAPendingOrder()
    {
        var harness = new OrderServiceHarness();

        var submitted = await harness.Service.SubmitAsync(
            OrderRequestBuilder.AnOrder().Build(), CancellationToken.None);

        return (harness, submitted.Order.Id);
    }

    [Fact]
    public async Task Confirming_a_pending_order_works_and_is_timestamped()
    {
        var (harness, orderId) = await WithAPendingOrder();
        harness.Clock.Advance(TimeSpan.FromHours(3));

        var result = await harness.Service.ChangeStatusAsync(
            orderId, OrderStatus.Confirmed, CancellationToken.None);

        result.Changed.Should().BeTrue();
        result.Order.Status.Should().Be("Confirmed");
        result.Order.UpdatedAtUtc.Should().Be(harness.Clock.UtcNow);
        result.Order.CreatedAtUtc.Should().BeBefore(result.Order.UpdatedAtUtc);
    }

    /// <summary>
    /// The response tells the client what it may do next, so the UI never has to
    /// reimplement the transition rules.
    /// </summary>
    [Fact]
    public async Task The_response_advertises_the_next_legal_steps()
    {
        var (harness, orderId) = await WithAPendingOrder();

        var confirmed = await harness.Service.ChangeStatusAsync(
            orderId, OrderStatus.Confirmed, CancellationToken.None);

        confirmed.Order.AllowedTransitions.Should().BeEquivalentTo(new[] { "Fulfilled", "Cancelled" });

        var fulfilled = await harness.Service.ChangeStatusAsync(
            orderId, OrderStatus.Fulfilled, CancellationToken.None);

        fulfilled.Order.AllowedTransitions.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirming_twice_succeeds_without_changing_anything()
    {
        var (harness, orderId) = await WithAPendingOrder();

        var first = await harness.Service.ChangeStatusAsync(
            orderId, OrderStatus.Confirmed, CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromHours(1));

        var second = await harness.Service.ChangeStatusAsync(
            orderId, OrderStatus.Confirmed, CancellationToken.None);

        first.Changed.Should().BeTrue();
        second.Changed.Should().BeFalse("the order was already in that status");
        second.Order.Status.Should().Be("Confirmed");
        second.Order.UpdatedAtUtc.Should().Be(first.Order.UpdatedAtUtc, "a no-op must not touch the timestamp");
    }

    [Fact]
    public async Task Skipping_confirmation_is_refused_with_guidance()
    {
        var (harness, orderId) = await WithAPendingOrder();

        var act = async () => await harness.Service.ChangeStatusAsync(
            orderId, OrderStatus.Fulfilled, CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<InvalidStatusTransitionException>()).Which;

        exception.CurrentStatus.Should().Be(OrderStatus.Pending);
        exception.TargetStatus.Should().Be(OrderStatus.Fulfilled);
        exception.AllowedTransitions.Should().BeEquivalentTo(
            new[] { OrderStatus.Confirmed, OrderStatus.Cancelled });
    }

    [Fact]
    public async Task A_cancelled_order_cannot_be_revived()
    {
        var (harness, orderId) = await WithAPendingOrder();

        await harness.Service.ChangeStatusAsync(orderId, OrderStatus.Cancelled, CancellationToken.None);

        var act = async () => await harness.Service.ChangeStatusAsync(
            orderId, OrderStatus.Confirmed, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidStatusTransitionException>()
            .WithMessage("*final state*");
    }

    [Fact]
    public async Task The_full_happy_path_runs_end_to_end()
    {
        var (harness, orderId) = await WithAPendingOrder();

        await harness.Service.ChangeStatusAsync(orderId, OrderStatus.Confirmed, CancellationToken.None);
        var final = await harness.Service.ChangeStatusAsync(orderId, OrderStatus.Fulfilled, CancellationToken.None);

        final.Order.Status.Should().Be("Fulfilled");

        var reloaded = await harness.Service.GetAsync(orderId, CancellationToken.None);
        reloaded.Status.Should().Be("Fulfilled");
    }

    [Fact]
    public async Task Changing_the_status_of_an_unknown_order_reports_not_found()
    {
        var harness = new OrderServiceHarness();

        var act = async () => await harness.Service.ChangeStatusAsync(
            Guid.NewGuid(), OrderStatus.Confirmed, CancellationToken.None);

        await act.Should().ThrowAsync<OrderNotFoundException>();
    }

    [Fact]
    public async Task Fetching_an_unknown_order_reports_not_found()
    {
        var harness = new OrderServiceHarness();

        var act = async () => await harness.Service.GetAsync(Guid.NewGuid(), CancellationToken.None);

        (await act.Should().ThrowAsync<OrderNotFoundException>()).Which
            .Code.Should().Be("ORDER_NOT_FOUND");
    }

    /// <summary>
    /// A cancelled order still occupies its reference. Re-submitting the same
    /// reference must not quietly reopen it — the rep needs a new reference,
    /// which is what the conflict message tells them.
    /// </summary>
    [Fact]
    public async Task A_cancelled_order_still_owns_its_reference()
    {
        var (harness, orderId) = await WithAPendingOrder();
        await harness.Service.ChangeStatusAsync(orderId, OrderStatus.Cancelled, CancellationToken.None);

        var resubmitted = await harness.Service.SubmitAsync(
            OrderRequestBuilder.AnOrder().Build(), CancellationToken.None);

        resubmitted.IsReplay.Should().BeTrue();
        resubmitted.Order.Id.Should().Be(orderId);
        resubmitted.Order.Status.Should().Be("Cancelled");
        harness.Orders.Count.Should().Be(1);
    }
}
