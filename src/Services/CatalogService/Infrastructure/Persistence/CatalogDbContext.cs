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

            entity.Property(x => x.Brand)
                .HasMaxLength(Product.MaxBrandLength);

            entity.OwnsMany(x => x.Attributes, attribute =>
            {
                attribute.ToTable("ProductAttributes");
                attribute.WithOwner().HasForeignKey("ProductId");
                attribute.Property<int>("Id");
                attribute.HasKey("Id");
                attribute.Property(a => a.Name).IsRequired().HasMaxLength(ProductAttribute.MaxNameLength);
                attribute.Property(a => a.Value).IsRequired().HasMaxLength(ProductAttribute.MaxValueLength);
            });
            entity.Navigation(x => x.Attributes).UsePropertyAccessMode(PropertyAccessMode.Field);

            entity.OwnsMany(x => x.Gallery, image =>
            {
                image.ToTable("ProductGalleryImages");
                image.WithOwner().HasForeignKey("ProductId");
                image.Property<int>("Id");
                image.HasKey("Id");
                image.Property(g => g.Version).IsRequired().HasMaxLength(64);
                image.Property(g => g.ContentType).IsRequired().HasMaxLength(50);
            });
            entity.Navigation(x => x.Gallery).UsePropertyAccessMode(PropertyAccessMode.Field);

            entity.OwnsOne(x => x.Discount, discount =>
            {
                discount.Property(d => d.Kind).IsRequired().HasConversion<string>().HasMaxLength(10);
                discount.Property(d => d.Value).HasPrecision(18, 2);
            });

            entity.HasIndex(x => x.Category);
            entity.HasIndex(x => x.Brand);
        });
    }
}
