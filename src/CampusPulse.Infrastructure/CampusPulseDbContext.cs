using CampusPulse.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Infrastructure;

/// <summary>Entities come from CampusPulse.Domain; their mappings go here.</summary>
public sealed class CampusPulseDbContext(DbContextOptions<CampusPulseDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

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
        });
    }
}
