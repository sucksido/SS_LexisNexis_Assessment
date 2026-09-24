using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderIntake.Application.Abstractions;
using OrderIntake.Application.Orders;
using OrderIntake.Application.Orders.Contracts;

namespace OrderIntake.Infrastructure.Persistence;

public static class DatabaseInitialiser
{
    /// <summary>
    /// Creates the schema and, optionally, seeds a few sample orders.
    ///
    /// EnsureCreated rather than Migrate: this app has no production database to
    /// evolve, and carrying a migrations folder for a take-home would be
    /// ceremony without benefit. Adding migrations later is a one-command change.
    /// </summary>
    public static async Task InitialiseAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();

        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<OrderIntakeDbContext>();
        var options = provider.GetRequiredService<StorageOptions>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseInitialiser));

        await db.Database.EnsureCreatedAsync(cancellationToken);

        if (!options.SeedSampleData || await db.Orders.AnyAsync(cancellationToken))
        {
            return;
        }

        var orderService = provider.GetRequiredService<IOrderService>();
        var clock = provider.GetRequiredService<IClock>();

        foreach (var sample in BuildSampleOrders())
        {
            await orderService.SubmitAsync(sample, cancellationToken);
        }

        logger.LogInformation("Seeded sample orders at {Timestamp}.", clock.UtcNow);
    }

    /// <summary>
    /// Seeded through the real service rather than by inserting rows directly, so
    /// the sample data is guaranteed to satisfy every invariant the API enforces.
    /// Seed data that could not have been created through the front door is seed
    /// data that will eventually lie to you.
    /// </summary>
    private static IEnumerable<SubmitOrderRequest> BuildSampleOrders()
    {
        yield return new SubmitOrderRequest
        {
            ExternalReference = "PO-1001",
            Currency = "USD",
            Notes = "Standard delivery.",
            Customer = new CustomerRequest { Email = "ada@contoso.com", Name = "Ada Lovelace" },
            Lines = new[]
            {
                new OrderLineRequest { Sku = "KB-001", Name = "Mechanical Keyboard", Quantity = 2, UnitPrice = 89.99m },
                new OrderLineRequest { Sku = "MS-014", Name = "Wireless Mouse", Quantity = 3, UnitPrice = 24.50m }
            }
        };

        yield return new SubmitOrderRequest
        {
            ExternalReference = "PO-1002",
            Currency = "USD",
            Notes = "Rush order - customer is on site Friday.",
            Customer = new CustomerRequest { Email = "grace@contoso.com", Name = "Grace Hopper" },
            Lines = new[]
            {
                new OrderLineRequest { Sku = "MON-27", Name = "27\" 4K Monitor", Quantity = 1, UnitPrice = 419.00m }
            }
        };

        yield return new SubmitOrderRequest
        {
            ExternalReference = "PO-1003",
            Currency = "ZAR",
            Customer = new CustomerRequest { Email = "ada@contoso.com", Name = "Ada Lovelace" },
            Lines = new[]
            {
                new OrderLineRequest { Sku = "DSK-STD", Name = "Standing Desk", Quantity = 1, UnitPrice = 7499.95m },
                new OrderLineRequest { Sku = "CHR-ERG", Name = "Ergonomic Chair", Quantity = 2, UnitPrice = 3250.00m }
            }
        };
    }
}
