using CatalogService.Domain;
using Microsoft.EntityFrameworkCore;

namespace CatalogService.Infrastructure.Persistence;

public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(Category.MaxNameLength);

            entity.Property(x => x.CreatedAtUtc)
                .IsRequired();

            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(x => x.Description)
                .IsRequired()
                .HasMaxLength(2000);

            entity.Property(x => x.Price)
                .IsRequired()
                .HasPrecision(18, 2);

            entity.Property(x => x.Category)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(x => x.CreatedAtUtc)
                .IsRequired();

            entity.Ignore(x => x.IsArchived);

            entity.Property(x => x.ImageContentType)
                .HasMaxLength(50);

            entity.Property(x => x.ImageVersion)
                .HasMaxLength(64);

            entity.HasIndex(x => x.Category);
        });
    }
}
