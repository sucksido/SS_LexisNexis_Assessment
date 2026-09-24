using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderIntake.Domain.Customers;
using OrderIntake.Domain.Orders;

namespace OrderIntake.Infrastructure.Persistence;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(order => order.Id);

        // Computed from Status by the status policy, so it is a view of state
        // rather than state. Said out loud here because EF Core 8 maps collections
        // of primitives by convention, and a silently persisted copy of a derived
        // value is exactly the sort of thing that goes stale without anyone noticing.
        builder.Ignore(order => order.AllowedTransitions);

        builder.Property(order => order.ExternalReference)
            .IsRequired()
            .HasMaxLength(64);

        // The rule the whole feature rests on, expressed where it can actually be
        // enforced. Scoped to the customer because "PO-1001" belongs to whoever
        // issued it — two customers may each have one.
        builder.HasIndex(order => new { order.CustomerId, order.ExternalReference })
            .IsUnique()
            .HasDatabaseName("UX_Orders_Customer_ExternalReference");

        // Stored as text. An integer column would silently remap every existing
        // row the day someone inserts a new member into the middle of the enum.
        builder.Property(order => order.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(order => order.Currency)
            .IsRequired()
            .HasMaxLength(3)
            .IsFixedLength();

        builder.Property(order => order.Notes)
            .HasMaxLength(1000);

        builder.Property(order => order.RequestFingerprint)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(order => order.Subtotal).HasPrecision(18, 2);
        builder.Property(order => order.Total).HasPrecision(18, 2);

        // Supports the default "newest first" listing.
        builder.HasIndex(order => order.CreatedAtUtc)
            .HasDatabaseName("IX_Orders_CreatedAtUtc");

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(order => order.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Lines are exposed as IReadOnlyList with no setter, so EF writes through
        // the _lines backing field. That is what keeps the aggregate's collection
        // genuinely private rather than private-in-name-only.
        builder.HasMany(order => order.Lines)
            .WithOne()
            .HasForeignKey(line => line.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(order => order.Lines)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .AutoInclude();
    }
}

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("OrderLines");
        builder.HasKey(line => line.Id);

        builder.Property(line => line.Sku).IsRequired().HasMaxLength(64);
        builder.Property(line => line.Name).IsRequired().HasMaxLength(200);
        builder.Property(line => line.Quantity).IsRequired();
        builder.Property(line => line.UnitPrice).HasPrecision(18, 2);
        builder.Property(line => line.LineTotal).HasPrecision(18, 2);

        builder.HasIndex(line => line.OrderId).HasDatabaseName("IX_OrderLines_OrderId");
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");
        builder.HasKey(customer => customer.Id);

        builder.Property(customer => customer.Email).IsRequired().HasMaxLength(256);
        builder.Property(customer => customer.Name).IsRequired().HasMaxLength(200);

        // Email is the natural key: it is what the sales rep knows and types.
        builder.HasIndex(customer => customer.Email)
            .IsUnique()
            .HasDatabaseName("UX_Customers_Email");
    }
}
