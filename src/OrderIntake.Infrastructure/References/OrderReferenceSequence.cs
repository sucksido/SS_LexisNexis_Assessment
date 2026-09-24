using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using OrderIntake.Application.Abstractions;

namespace OrderIntake.Infrastructure.References;

/// <summary>
/// Issues references of the form <c>PO-000123</c>.
///
/// Registered as a singleton, because a counter that is not shared is not a
/// counter. It seeds itself on first use from the highest number already in the
/// store, so a restart resumes rather than re-issuing numbers that are taken,
/// and so the sample data does not have to be special-cased.
///
/// Three limits, stated rather than discovered:
///
/// 1. <see cref="Interlocked.Increment(ref long)"/> makes this atomic within one
///    process and nowhere else. On two API instances both will hand out the same
///    number, and the second submission to arrive gets a 409. A real deployment
///    wants a database sequence — <c>CREATE SEQUENCE</c>, or an identity column —
///    which is one method body away because of the interface.
/// 2. Numbers are issued when the form opens, not when it is submitted, so
///    abandoned forms leave gaps. That is normal for purchase orders and the
///    alternative (allocating on submit) would mean the rep never sees the number
///    they are about to create.
/// 3. The seed is a prefix scan. It happens once per process, so it is cheap in
///    the way that only once-per-process things are.
/// </summary>
public sealed class OrderReferenceSequence : IOrderReferenceSequence
{
    /// <summary>Kept in step with the sample data, which uses PO-1001 upward.</summary>
    public const string Prefix = "PO-";

    private const string NumberFormat = "000000";

    /// <summary>Not seeded yet. Negative so that "no orders at all" can be zero.</summary>
    private const long Unseeded = -1;

    private readonly IServiceScopeFactory _scopes;
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    private long _last = Unseeded;

    public OrderReferenceSequence(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<string> NextAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _last) == Unseeded)
        {
            await SeedAsync(cancellationToken);
        }

        return Format(Interlocked.Increment(ref _last));
    }

    public static string Format(long number) =>
        Prefix + number.ToString(NumberFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the highest number already issued.
    ///
    /// Tolerant on the way in and strict on the way out: anything after the
    /// prefix that is not a plain number is ignored rather than throwing, because
    /// a store may well contain references typed by a human under the old
    /// behaviour — and one legacy row shaped like <c>PO-URGENT</c> must not stop
    /// the form from loading.
    /// </summary>
    public static long HighestNumberIn(IEnumerable<string> references)
    {
        var highest = 0L;

        foreach (var reference in references)
        {
            if (reference is null || !reference.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var digits = reference.AsSpan(Prefix.Length);

            if (digits.Length is 0 or > 18)
            {
                continue;
            }

            if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
                number > highest)
            {
                highest = number;
            }
        }

        return highest;
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await _seedLock.WaitAsync(cancellationToken);

        try
        {
            // Re-checked inside the lock: several callers can arrive together on
            // a cold start, and only the first should read the store.
            if (Volatile.Read(ref _last) != Unseeded)
            {
                return;
            }

            // A singleton cannot hold a scoped DbContext, so it borrows a scope
            // for the one read it ever does.
            using var scope = _scopes.CreateScope();
            var orders = scope.ServiceProvider.GetRequiredService<IOrderRepository>();

            var existing = await orders.ListReferencesStartingWithAsync(Prefix, cancellationToken);

            Volatile.Write(ref _last, HighestNumberIn(existing));
        }
        finally
        {
            _seedLock.Release();
        }
    }
}
