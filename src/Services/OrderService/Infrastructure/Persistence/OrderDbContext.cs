using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderService.Application.Sagas;
using OrderService.Domain;

namespace OrderService.Infrastructure.Persistence;

public class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderSagaState> OrderSagaStates => Set<OrderSagaState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.UserId)
                .IsRequired();

            entity.Property(x => x.Status)
                .IsRequired()
                .HasConversion<string>()
                .HasMaxLength(20);

            entity.Property(x => x.CreatedAtUtc)
                .IsRequired();

            entity.Ignore(x => x.TotalAmount);

            // The admin analytics filter and group by creation time and status; "my orders"
            // filters by user. Without these every one of them scans the whole table.
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasIndex(x => new { x.Status, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.UserId, x.CreatedAtUtc });

            entity.OwnsMany(x => x.Items, item =>
            {
                item.WithOwner().HasForeignKey("OrderId");
                item.Property<Guid>("Id");
                item.HasKey("Id");

                item.Property(i => i.ProductId).IsRequired();
                item.Property(i => i.Quantity).IsRequired();
                item.Property(i => i.UnitPrice).IsRequired().HasPrecision(18, 2);
            });
        });

        modelBuilder.Entity<OrderSagaState>(entity =>
        {
            entity.HasKey(x => x.CorrelationId);
            entity.Property(x => x.CurrentState).IsRequired().HasMaxLength(64);
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.Property(x => x.ItemsJson).IsRequired();
            entity.Property(x => x.ReservedProductIdsJson).IsRequired();
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }
}
