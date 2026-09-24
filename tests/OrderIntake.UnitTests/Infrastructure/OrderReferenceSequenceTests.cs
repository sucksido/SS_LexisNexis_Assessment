using Microsoft.Extensions.DependencyInjection;
using OrderIntake.Application.Abstractions;
using OrderIntake.Infrastructure.References;
using OrderIntake.UnitTests.Support;

namespace OrderIntake.UnitTests.Infrastructure;

/// <summary>
/// The sequence behind the read-only Reference box on the intake form.
///
/// Two things are worth protecting here and they pull in opposite directions.
/// It has to resume from what is already stored, or a restart starts handing
/// out numbers that are taken; and it has to survive a store containing
/// references that predate it, because the form used to let reps type whatever
/// they liked and those rows do not disappear.
/// </summary>
public class OrderReferenceSequenceTests
{
    [Fact]
    public async Task An_empty_store_starts_at_one()
    {
        var sequence = SequenceOver(new FakeOrderRepository());

        (await sequence.NextAsync(default)).Should().Be("PO-000001");
    }

    [Fact]
    public async Task Numbers_are_handed_out_in_order_and_never_repeated()
    {
        var sequence = SequenceOver(new FakeOrderRepository());

        var issued = new[]
        {
            await sequence.NextAsync(default),
            await sequence.NextAsync(default),
            await sequence.NextAsync(default)
        };

        issued.Should().Equal("PO-000001", "PO-000002", "PO-000003");
    }

    [Fact]
    public async Task It_resumes_above_the_highest_reference_already_stored()
    {
        // Seeded through the real service, so these are orders that could
        // actually exist rather than rows a test invented.
        var harness = new OrderServiceHarness();
        await harness.Service.SubmitAsync(OrderRequestBuilder.AnOrder().WithReference("PO-000007").Build(), default);
        await harness.Service.SubmitAsync(OrderRequestBuilder.AnOrder().WithReference("PO-000042").Build(), default);
        await harness.Service.SubmitAsync(OrderRequestBuilder.AnOrder().WithReference("PO-000013").Build(), default);

        var sequence = SequenceOver(harness.Orders);

        // Highest wins, not most-recently-created: the store has no order.
        (await sequence.NextAsync(default)).Should().Be("PO-000043");
    }

    [Fact]
    public async Task Concurrent_callers_never_receive_the_same_number()
    {
        var sequence = SequenceOver(new FakeOrderRepository());

        // Also exercises the cold-start path: all fifty arrive before the seed
        // has finished, and only one of them may perform it.
        var issued = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => Task.Run(() => sequence.NextAsync(default))));

        issued.Should().OnlyHaveUniqueItems();
        issued.Should().HaveCount(50);
    }

    [Theory]
    [InlineData("PO-000001", 1)]
    [InlineData("PO-1003", 1003)]
    [InlineData("PO-000000", 0)]
    public void A_well_formed_reference_yields_its_number(string reference, long expected) =>
        OrderReferenceSequence.HighestNumberIn(new[] { reference }).Should().Be(expected);

    [Theory]
    [InlineData("PO-URGENT")]        // a human typed it under the old behaviour
    [InlineData("PO-")]              // prefix and nothing else
    [InlineData("PO-12A")]           // nearly a number
    [InlineData("PO--5")]            // negative, which would move the counter backwards
    [InlineData("PO- 12")]           // leading space
    [InlineData("INV-000009")]       // not ours at all
    [InlineData("PO-99999999999999999999")] // wider than a long
    public void A_reference_that_is_not_a_number_is_ignored_rather_than_fatal(string reference) =>
        OrderReferenceSequence.HighestNumberIn(new[] { reference }).Should().Be(0);

    [Fact]
    public void Junk_alongside_real_references_does_not_hide_them()
    {
        var references = new[] { "PO-URGENT", "PO-000009", "INV-1", "PO-000004" };

        OrderReferenceSequence.HighestNumberIn(references).Should().Be(9);
    }

    [Fact]
    public void Numbers_are_padded_so_references_sort_the_way_they_read() =>
        new[] { 1L, 42L, 1003L }
            .Select(OrderReferenceSequence.Format)
            .Should().Equal("PO-000001", "PO-000042", "PO-001003");

    /// <summary>
    /// The sequence takes an <see cref="IServiceScopeFactory"/> because a
    /// singleton cannot hold a scoped repository. A real container is the
    /// honest way to satisfy that — faking the scope factory would test the
    /// fake.
    /// </summary>
    private static OrderReferenceSequence SequenceOver(IOrderRepository orders)
    {
        var provider = new ServiceCollection()
            .AddScoped(_ => orders)
            .BuildServiceProvider();

        return new OrderReferenceSequence(provider.GetRequiredService<IServiceScopeFactory>());
    }
}
