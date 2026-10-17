using MassTransit;
using Microsoft.EntityFrameworkCore;
using UserService.Domain;

namespace UserService.Infrastructure.Persistence;

public class UserDbContext : DbContext
{
    public UserDbContext(DbContextOptions<UserDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Email)
                .IsRequired()
                .HasMaxLength(320);

            entity.HasIndex(x => x.Email).IsUnique();

            entity.Property(x => x.DisplayName)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(x => x.Role)
                .IsRequired()
                .HasConversion<string>()
                .HasMaxLength(20);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(x => x.Id);

            // SHA-256 as hex is 64 characters. Unique, and the index is how a presented token is found.
            entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);
            entity.HasIndex(x => x.TokenHash).IsUnique();

            // Ending a sign-in updates every token of its family; the sweeper finds expired ones.
            entity.HasIndex(x => x.FamilyId);
            entity.HasIndex(x => x.ExpiresAtUtc);

            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
