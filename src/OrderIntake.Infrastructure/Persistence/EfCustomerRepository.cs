using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderIntake.Application.Abstractions;
using OrderIntake.Domain.Customers;

namespace OrderIntake.Infrastructure.Persistence;

public sealed class EfCustomerRepository : ICustomerRepository
{
    private readonly OrderIntakeDbContext _db;
    private readonly IIdempotencyGate _gate;

    public EfCustomerRepository(OrderIntakeDbContext db, IIdempotencyGate gate)
    {
        _db = db;
        _gate = gate;
    }

    public Task<Customer?> GetByIdAsync(Guid customerId, CancellationToken cancellationToken) =>
        _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);

    /// <summary>
    /// Get-or-create by normalised email.
    ///
    /// This is a check-then-act in its own right — two first-time orders from the
    /// same new customer arriving together would both find nothing and both
    /// insert. The same two defences apply as for orders: the gate serialises
    /// callers in-process, and the unique index on Email catches anything that
    /// gets past it, at which point we simply read the winner.
    ///
    /// The gate lives in the repository rather than in OrderService because
    /// get-or-create is a single atomic operation from the caller's point of
    /// view. The order-level gate has to span two repository calls, so it cannot.
    /// </summary>
    public async Task<Customer> GetOrCreateAsync(
        string email,
        string name,
        CancellationToken cancellationToken)
    {
        var normalisedEmail = Customer.NormaliseEmail(email);

        await using (await _gate.AcquireAsync($"customer:{normalisedEmail}", cancellationToken))
        {
            var existing = await _db.Customers
                .FirstOrDefaultAsync(c => c.Email == normalisedEmail, cancellationToken);

            if (existing is not null)
            {
                // Keep the display name fresh without changing identity.
                existing.UpdateName(name);
                await _db.SaveChangesAsync(cancellationToken);

                return existing;
            }

            var created = Customer.Create(normalisedEmail, name);
            _db.Customers.Add(created);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                _db.Entry(created).State = EntityState.Detached;

                return await _db.Customers
                    .FirstAsync(c => c.Email == normalisedEmail, cancellationToken);
            }

            return created;
        }
    }
}
