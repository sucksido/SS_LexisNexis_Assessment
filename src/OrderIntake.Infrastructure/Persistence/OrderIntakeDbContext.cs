using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OrderIntake.Domain.Customers;
using OrderIntake.Domain.Orders;

namespace OrderIntake.Infrastructure.Persistence;

public sealed class OrderIntakeDbContext : DbContext
{
    public OrderIntakeDbContext(DbContextOptions<OrderIntakeDbContext> options) : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new OrderConfiguration());
        modelBuilder.ApplyConfiguration(new OrderLineConfiguration());
        modelBuilder.ApplyConfiguration(new CustomerConfiguration());

        ApplySqliteWorkarounds(modelBuilder);
    }

    /// <summary>
    /// SQLite has no native date/time type, so EF Core cannot translate an
    /// ORDER BY over a DateTimeOffset column — and "newest first" is the one
    /// sort this application actually needs. Storing UTC ticks as an integer
    /// makes the ordering both translatable and correct.
    ///
    /// Applied only on SQLite: the in-memory provider handles DateTimeOffset
    /// natively and gains nothing from the conversion.
    /// </summary>
    private void ApplySqliteWorkarounds(ModelBuilder modelBuilder)
    {
        if (!Database.IsSqlite())
        {
            return;
        }

        var converter = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(converter);
                }
            }
        }
    }
}
