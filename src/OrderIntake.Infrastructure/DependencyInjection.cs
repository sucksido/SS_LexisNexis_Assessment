using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderIntake.Application.Abstractions;
using OrderIntake.Infrastructure.Idempotency;
using OrderIntake.Infrastructure.Persistence;
using OrderIntake.Infrastructure.References;

namespace OrderIntake.Infrastructure;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>"InMemory" (default, zero setup) or "Sqlite" (survives a restart).</summary>
    public string Provider { get; set; } = "InMemory";

    public string SqliteConnectionString { get; set; } = "Data Source=orderintake.db";

    /// <summary>Writes a few example orders on first run so the UI is not empty.</summary>
    public bool SeedSampleData { get; set; } = true;
}

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers persistence.
    ///
    /// Two providers behind one DbContext. In-memory is the default because the
    /// brief asks for minimal setup and a reviewer should be able to clone and
    /// run. SQLite exists because the in-memory provider does not enforce unique
    /// indexes, and the duplicate-prevention story is only complete when you can
    /// show it holding at the database level too. One config value switches them.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()
                      ?? new StorageOptions();

        services.AddSingleton(storage);

        services.AddDbContext<OrderIntakeDbContext>(options =>
        {
            if (string.Equals(storage.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                options.UseSqlite(storage.SqliteConnectionString);
            }
            else
            {
                // A fixed database name means every scoped DbContext in the
                // process shares one store, which is what makes the provider
                // behave like a real (if forgetful) database.
                options.UseInMemoryDatabase("order-intake");
            }
        });

        // Singleton: the whole point is that every request in the process
        // contends on the same set of keys.
        services.AddSingleton<IIdempotencyGate, KeyedIdempotencyGate>();

        // Singleton for the same reason, and for a second one: it seeds itself
        // from the store on first use, and a per-request instance would repeat
        // that scan on every request and still hand out the same number twice.
        services.AddSingleton<IOrderReferenceSequence, OrderReferenceSequence>();

        services.AddScoped<IOrderRepository, EfOrderRepository>();
        services.AddScoped<ICustomerRepository, EfCustomerRepository>();

        return services;
    }
}
