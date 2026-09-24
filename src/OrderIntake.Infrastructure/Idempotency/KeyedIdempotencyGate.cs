using System.Collections.Concurrent;
using OrderIntake.Application.Abstractions;

namespace OrderIntake.Infrastructure.Idempotency;

/// <summary>
/// An in-process, per-key async mutex.
///
/// Two requests carrying the same idempotency key are serialised; requests with
/// different keys never touch each other, so this does not become a global
/// bottleneck the way a single lock would.
///
/// Scope, stated plainly: this only coordinates threads inside one process. Run
/// two instances behind a load balancer and it protects nothing. That is fine,
/// because the unique index in the store is the authoritative guard and
/// OrderService handles losing that race. This gate exists to make the common
/// case (a double-click hitting one instance) resolve cleanly, and to give the
/// default in-memory configuration — whose provider ignores unique indexes —
/// something real to stand on. The distributed equivalent is a Redis lock or
/// simply leaning on the database constraint alone.
///
/// Entries are reference-counted and removed once idle, so a long-running
/// process does not accumulate one semaphore per order reference ever seen.
/// </summary>
public sealed class KeyedIdempotencyGate : IIdempotencyGate
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public readonly object SyncRoot = new();
        public int RefCount;
        public bool Evicted;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Exposed for tests: asserts that the gate does not leak entries.</summary>
    public int TrackedKeyCount => _entries.Count;

    public async Task<IAsyncDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var entry = Rent(key);

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled while queueing: give the reference back so the entry can
            // still be evicted, then let the cancellation propagate.
            Release(key, entry);
            throw;
        }

        return new Lease(this, key, entry);
    }

    private Entry Rent(string key)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(key, _ => new Entry());

            lock (entry.SyncRoot)
            {
                if (!entry.Evicted)
                {
                    entry.RefCount++;
                    return entry;
                }
            }

            // We grabbed an entry that another thread evicted between our lookup
            // and our lock. It is no longer in the dictionary, so retrying gets
            // us a fresh one.
        }
    }

    private void Release(string key, Entry entry)
    {
        lock (entry.SyncRoot)
        {
            entry.RefCount--;

            if (entry.RefCount > 0)
            {
                return;
            }

            // Last one out: evict and dispose. Safe because a reference is taken
            // before waiting, so nobody can be queued on this semaphore now.
            entry.Evicted = true;
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly KeyedIdempotencyGate _gate;
        private readonly string _key;
        private readonly Entry _entry;
        private int _disposed;

        public Lease(KeyedIdempotencyGate gate, string key, Entry entry)
        {
            _gate = gate;
            _key = key;
            _entry = entry;
        }

        public ValueTask DisposeAsync()
        {
            // Guarded so a double dispose cannot inflate the semaphore count.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _entry.Semaphore.Release();
                _gate.Release(_key, _entry);
            }

            return ValueTask.CompletedTask;
        }
    }
}
