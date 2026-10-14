using CampusPulse.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Infrastructure;

/// <summary>Entities come from CampusPulse.Domain; their mappings go here.</summary>
public sealed class CampusPulseDbContext(DbContextOptions<CampusPulseDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<AccountDeletionCount> AccountDeletionCounts => Set<AccountDeletionCount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users");
            user.HasKey(u => u.Id);
            user.Property(u => u.Id).ValueGeneratedNever();
            user.Property(u => u.EntraObjectId).HasMaxLength(User.EntraObjectIdMaxLength).IsRequired();
            // One row per Microsoft account, enforced by the database: two first sign-ins at the
            // same moment can't both insert.
            user.HasIndex(u => u.EntraObjectId).IsUnique();
            user.Property(u => u.Email).HasMaxLength(User.EmailMaxLength).IsRequired();
            user.Property(u => u.DisplayName).HasMaxLength(User.DisplayNameMaxLength).IsRequired();
            // Every table holding a student's data refers to this one with ON DELETE CASCADE, so
            // deleting the row deletes the account (AccountDeletion). A test fails otherwise.
        });

        modelBuilder.Entity<AccountDeletionCount>(count =>
        {
            count.ToTable("account_deletion_counts");
            count.HasKey(c => c.Day);
            count.Property(c => c.Day).ValueGeneratedNever();
        });
    }
}
