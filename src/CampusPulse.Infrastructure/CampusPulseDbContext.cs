using CampusPulse.Domain.Push;
using CampusPulse.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Infrastructure;

/// <summary>Entities come from CampusPulse.Domain; their mappings go here.</summary>
public sealed class CampusPulseDbContext(DbContextOptions<CampusPulseDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<AccountDeletionCount> AccountDeletionCounts => Set<AccountDeletionCount>();

    public DbSet<PushDevice> PushDevices => Set<PushDevice>();

    public DbSet<PushReceiptPending> PushReceiptsPending => Set<PushReceiptPending>();

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

        modelBuilder.Entity<PushDevice>(device =>
        {
            device.ToTable("push_devices");
            device.HasKey(d => d.Id);
            device.Property(d => d.Id).ValueGeneratedNever();
            device.HasOne<User>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
            device.Property(d => d.Token).HasMaxLength(PushDevice.TokenMaxLength).IsRequired();
            // One row per app install: a token registered by another student moves to them.
            device.HasIndex(d => d.Token).IsUnique();
            // The per-student device cap drops the least recently registered.
            device.HasIndex(d => new { d.UserId, d.LastRegisteredAt });
            device.Property(d => d.Platform).HasMaxLength(10).HasConversion(
                platform => platform == PushPlatform.Ios ? "ios" : "android",
                code => code == "ios" ? PushPlatform.Ios : PushPlatform.Android);
        });

        modelBuilder.Entity<PushReceiptPending>(receipt =>
        {
            receipt.ToTable("push_receipts_pending");
            receipt.HasKey(r => r.TicketId);
            receipt.Property(r => r.TicketId).HasMaxLength(PushReceiptPending.TicketIdMaxLength).ValueGeneratedNever();
            receipt.HasOne<PushDevice>().WithMany().HasForeignKey(r => r.DeviceId).OnDelete(DeleteBehavior.Cascade);
            receipt.HasIndex(r => r.CreatedAt);
        });
    }
}
