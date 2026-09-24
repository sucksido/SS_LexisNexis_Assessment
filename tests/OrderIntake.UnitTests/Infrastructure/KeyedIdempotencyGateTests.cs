using System.Diagnostics;
using OrderIntake.Infrastructure.Idempotency;

namespace OrderIntake.UnitTests.Infrastructure;

public sealed class KeyedIdempotencyGateTests
{
    [Fact]
    public async Task Callers_sharing_a_key_are_serialised()
    {
        var gate = new KeyedIdempotencyGate();
        var concurrent = 0;
        var maxObserved = 0;

        async Task Contend()
        {
            await using (await gate.AcquireAsync("same-key", CancellationToken.None))
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxObserved, now);

                await Task.Delay(20);

                Interlocked.Decrement(ref concurrent);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(Contend)));

        maxObserved.Should().Be(1, "the whole point is that only one caller holds a given key at a time");
    }

    /// <summary>
    /// A single global lock would also pass the test above. This one fails if the
    /// gate is not actually keyed: twenty holders across twenty keys must be able
    /// to make progress at the same time.
    /// </summary>
    [Fact]
    public async Task Different_keys_do_not_block_each_other()
    {
        var gate = new KeyedIdempotencyGate();
        var everyoneIsIn = new TaskCompletionSource();
        var arrived = 0;
        const int Holders = 20;

        async Task Hold(int index)
        {
            await using (await gate.AcquireAsync($"key-{index}", CancellationToken.None))
            {
                if (Interlocked.Increment(ref arrived) == Holders)
                {
                    everyoneIsIn.SetResult();
                }

                // If the gate serialised across keys this would deadlock, so the
                // timeout below is the assertion.
                await everyoneIsIn.Task;
            }
        }

        var all = Task.WhenAll(Enumerable.Range(0, Holders).Select(index => Task.Run(() => Hold(index))));

        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().BeSameAs(all, "all twenty keys should be held simultaneously");
        await all;
    }

    /// <summary>
    /// A naive implementation keeps one semaphore per key forever, which turns a
    /// long-running process into a slow memory leak. Entries must be reclaimed.
    /// </summary>
    [Fact]
    public async Task Entries_are_reclaimed_once_nobody_holds_them()
    {
        var gate = new KeyedIdempotencyGate();

        for (var i = 0; i < 500; i++)
        {
            await using (await gate.AcquireAsync($"key-{i}", CancellationToken.None))
            {
                // Do nothing; the point is the acquire/release cycle.
            }
        }

        gate.TrackedKeyCount.Should().Be(0);
    }

    [Fact]
    public async Task Waiting_can_be_cancelled_without_stranding_the_entry()
    {
        var gate = new KeyedIdempotencyGate();

        var holder = await gate.AcquireAsync("busy", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () => await gate.AcquireAsync("busy", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        await holder.DisposeAsync();

        gate.TrackedKeyCount.Should().Be(0, "a cancelled waiter must still give its reference back");
    }

    [Fact]
    public async Task A_lease_can_be_disposed_twice_without_corrupting_the_count()
    {
        var gate = new KeyedIdempotencyGate();

        var lease = await gate.AcquireAsync("key", CancellationToken.None);
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        // If the double dispose had released the semaphore twice, two callers
        // could now enter at once.
        var stopwatch = Stopwatch.StartNew();
        var first = await gate.AcquireAsync("key", CancellationToken.None);

        var secondAcquired = gate.AcquireAsync("key", CancellationToken.None);
        var raced = await Task.WhenAny(secondAcquired, Task.Delay(200));

        raced.Should().NotBeSameAs(secondAcquired, "the second caller must still be blocked");
        stopwatch.Stop();

        await first.DisposeAsync();
        await (await secondAcquired).DisposeAsync();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int observed;

        do
        {
            observed = Volatile.Read(ref target);

            if (observed >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, observed) != observed);
    }
}
