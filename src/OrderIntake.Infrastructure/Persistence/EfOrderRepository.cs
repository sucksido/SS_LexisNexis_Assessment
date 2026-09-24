using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderIntake.Application.Abstractions;
using OrderIntake.Application.Common;
using OrderIntake.Application.Orders.Contracts;
using OrderIntake.Domain.Orders;

namespace OrderIntake.Infrastructure.Persistence;

public sealed class EfOrderRepository : IOrderRepository
{
    private readonly OrderIntakeDbContext _db;

    public EfOrderRepository(OrderIntakeDbContext db) => _db = db;

    public Task<Order?> FindByReferenceAsync(
        Guid customerId,
        string externalReference,
        CancellationToken cancellationToken) =>
        _db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(
                order => order.CustomerId == customerId && order.ExternalReference == externalReference,
                cancellationToken);

    /// <summary>
    /// Tracked on purpose: the caller may go on to change the status, and a
    /// detached entity would make that a silent no-op.
    /// </summary>
    public Task<Order?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken) =>
        _db.Orders.FirstOrDefaultAsync(order => order.Id == orderId, cancellationToken);

    public async Task<PagedResult<OrderSummary>> ListAsync(
        ListOrdersQuery query,
        CancellationToken cancellationToken)
    {
        // IgnoreAutoIncludes: the summary projection never reads line details,
        // and pulling every line of every order to render a list would be a
        // needless read amplification.
        var orders = _db.Orders.AsNoTracking().IgnoreAutoIncludes();

        if (query.Status is { } status)
        {
            orders = orders.Where(order => order.Status == status);
        }

        var joined =
            from order in orders
            join customer in _db.Customers.AsNoTracking() on order.CustomerId equals customer.Id
            select new { Order = order, Customer = customer };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();

            joined = joined.Where(row =>
                row.Order.ExternalReference.ToLower().Contains(term) ||
                row.Customer.Name.ToLower().Contains(term) ||
                row.Customer.Email.Contains(term));
        }

        // Counted before paging so the client can render "page 2 of 7".
        var totalCount = await joined.CountAsync(cancellationToken);

        var items = await joined
            .OrderByDescending(row => row.Order.CreatedAtUtc)
            // Deterministic tie-break. Without it, two orders created in the same
            // tick can swap places between page requests and the user sees a
            // duplicate on one page and a gap on the next.
            .ThenByDescending(row => row.Order.ExternalReference)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(row => new OrderSummary
            {
                Id = row.Order.Id,
                ExternalReference = row.Order.ExternalReference,
                CustomerName = row.Customer.Name,
                CustomerEmail = row.Customer.Email,
                Status = row.Order.Status.ToString(),
                Currency = row.Order.Currency,
                Total = row.Order.Total,
                LineCount = row.Order.Lines.Count(),
                CreatedAtUtc = row.Order.CreatedAtUtc,
                UpdatedAtUtc = row.Order.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<OrderSummary>
        {
            Items = items,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = totalCount
        };
    }

    public async Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        _db.Orders.Add(order);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Translate the provider's failure into a vocabulary the application
            // layer understands. Detaching first matters: leaving the rejected
            // entity tracked means the next SaveChanges on this scoped context
            // would try to insert it all over again.
            Detach(order);

            throw new DuplicateReferenceException(order.ExternalReference, ex);
        }
    }

    public Task UpdateAsync(Order order, CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// A prefix scan, which is a table scan on any store without an index on
    /// ExternalReference alone. Acceptable because the sequence calls this once
    /// per process, at startup, and never again. If it ever became a per-request
    /// call it would need a real database sequence instead — which is what it
    /// should be in production anyway.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListReferencesStartingWithAsync(
        string prefix,
        CancellationToken cancellationToken) =>
        await _db.Orders
            .AsNoTracking()
            .IgnoreAutoIncludes()
            .Where(order => order.ExternalReference.StartsWith(prefix))
            .Select(order => order.ExternalReference)
            .ToListAsync(cancellationToken);

    private void Detach(Order order)
    {
        foreach (var line in order.Lines)
        {
            _db.Entry(line).State = EntityState.Detached;
        }

        _db.Entry(order).State = EntityState.Detached;
    }

    /// <summary>
    /// SQLite reports any constraint failure as SQLITE_CONSTRAINT (19).
    ///
    /// Provider-specific detection is unavoidable here, and it is precisely why
    /// this lives behind IOrderRepository: adding SQL Server later means adding a
    /// clause to this method, not touching OrderService.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };
}
