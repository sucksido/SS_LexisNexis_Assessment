using OrderIntake.Domain.Orders;

namespace OrderIntake.UnitTests.Domain;

public sealed class OrderStatusTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Created.AddHours(2);

    private static Order PendingOrder() => Order.Create(
        Guid.NewGuid(), "PO-1001", "USD", null,
        new[] { new OrderLineDraft("SKU", "Item", 1, 10m) }, "fp", Created);

    private static Order OrderIn(OrderStatus status)
    {
        var order = PendingOrder();

        // Walk the legal path rather than reaching in and setting the field.
        // A test that can construct an impossible state proves nothing about
        // the states the system can actually reach.
        foreach (var step in PathTo(status))
        {
            order.ChangeStatus(step, Created);
        }

        return order;
    }

    private static IEnumerable<OrderStatus> PathTo(OrderStatus target) => target switch
    {
        OrderStatus.Pending => Array.Empty<OrderStatus>(),
        OrderStatus.Confirmed => new[] { OrderStatus.Confirmed },
        OrderStatus.Cancelled => new[] { OrderStatus.Cancelled },
        OrderStatus.Fulfilled => new[] { OrderStatus.Confirmed, OrderStatus.Fulfilled },
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
    };

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Pending, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Fulfilled)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Cancelled)]
    public void Legal_transitions_are_applied(OrderStatus from, OrderStatus to)
    {
        var order = OrderIn(from);

        var outcome = order.ChangeStatus(to, Later);

        outcome.Should().Be(StatusChangeOutcome.Changed);
        order.Status.Should().Be(to);
        order.UpdatedAtUtc.Should().Be(Later);
    }

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Fulfilled)]      // no skipping confirmation
    [InlineData(OrderStatus.Fulfilled, OrderStatus.Cancelled)]    // already shipped
    [InlineData(OrderStatus.Fulfilled, OrderStatus.Pending)]      // no going back
    [InlineData(OrderStatus.Fulfilled, OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Confirmed)]    // no resurrection
    [InlineData(OrderStatus.Cancelled, OrderStatus.Fulfilled)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Pending)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Pending)]
    public void Illegal_transitions_are_refused_and_the_order_is_untouched(OrderStatus from, OrderStatus to)
    {
        var order = OrderIn(from);
        var updatedBefore = order.UpdatedAtUtc;

        var act = () => order.ChangeStatus(to, Later);

        act.Should().Throw<InvalidStatusTransitionException>();

        order.Status.Should().Be(from);
        order.UpdatedAtUtc.Should().Be(updatedBefore, "a rejected change must not leave a trace");
    }

    /// <summary>
    /// The brief asks for a helpful message when a change does not make sense.
    /// "Not allowed" is not helpful; naming the alternatives is.
    /// </summary>
    [Fact]
    public void A_refusal_names_the_alternatives()
    {
        var order = OrderIn(OrderStatus.Pending);

        var act = () => order.ChangeStatus(OrderStatus.Fulfilled, Later);

        var exception = act.Should().Throw<InvalidStatusTransitionException>().Which;

        exception.Code.Should().Be("INVALID_STATUS_TRANSITION");
        exception.AllowedTransitions.Should().BeEquivalentTo(
            new[] { OrderStatus.Confirmed, OrderStatus.Cancelled });
        exception.Message.Should().Contain("Confirmed").And.Contain("Cancelled");
    }

    [Fact]
    public void A_refusal_from_a_final_state_says_so_rather_than_listing_nothing()
    {
        var order = OrderIn(OrderStatus.Fulfilled);

        var act = () => order.ChangeStatus(OrderStatus.Pending, Later);

        act.Should().Throw<InvalidStatusTransitionException>()
            .WithMessage("*final state*");
    }

    /// <summary>
    /// Re-confirming an already-confirmed order is a double-click, not an error.
    /// Same reasoning as duplicate submission: the outcome the caller asked for
    /// already holds.
    /// </summary>
    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Fulfilled)]
    [InlineData(OrderStatus.Cancelled)]
    public void Asking_for_the_current_status_succeeds_without_writing(OrderStatus status)
    {
        var order = OrderIn(status);
        var updatedBefore = order.UpdatedAtUtc;

        var outcome = order.ChangeStatus(status, Later);

        outcome.Should().Be(StatusChangeOutcome.AlreadyInStatus);
        order.Status.Should().Be(status);
        order.UpdatedAtUtc.Should().Be(updatedBefore);
    }

    [Theory]
    [InlineData(OrderStatus.Fulfilled)]
    [InlineData(OrderStatus.Cancelled)]
    public void Terminal_statuses_offer_no_way_out(OrderStatus status)
    {
        OrderStatusPolicy.IsTerminal(status).Should().BeTrue();
        OrderStatusPolicy.AllowedTransitionsFrom(status).Should().BeEmpty();
    }

    [Fact]
    public void An_order_reports_what_it_can_become_next()
    {
        PendingOrder().AllowedTransitions.Should().BeEquivalentTo(
            new[] { OrderStatus.Confirmed, OrderStatus.Cancelled });
    }

    /// <summary>
    /// Guards against a future enum member being added without a matching entry
    /// in the policy map, which would silently make it an unreachable dead end.
    /// </summary>
    [Fact]
    public void Every_status_has_an_entry_in_the_policy()
    {
        foreach (var status in Enum.GetValues<OrderStatus>())
        {
            var act = () => OrderStatusPolicy.AllowedTransitionsFrom(status);
            act.Should().NotThrow();
        }

        OrderStatusPolicy.AllowedTransitionsFrom(OrderStatus.Pending).Should().NotBeEmpty();
    }
}
