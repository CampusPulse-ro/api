using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CampusPulse.Api.Tests;

/// <summary>
/// DELETE /me deletes the <c>users</c> row and relies on the database to delete everything that
/// refers to it, so every foreign key to <c>users</c> must be ON DELETE CASCADE. Today there is none;
/// this fails the build when a later table (settings, device tokens, Campus Virtual tokens) forgets.
/// </summary>
public sealed class PerUserDataTests
{
    /// <summary>The foreign keys to <c>users</c> that would leave rows behind (or block the delete),
    /// described for the failure message. Only Cascade is a database-side ON DELETE CASCADE:
    /// ClientCascade happens in EF's change tracker, which the delete doesn't use.</summary>
    public static IEnumerable<string> ForeignKeysToUsersThatDontCascade(IModel model) =>
        model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(User) && fk.DeleteBehavior != DeleteBehavior.Cascade)
            .Select(fk => $"{fk.DeclaringEntityType.GetTableName()} ({string.Join(", ", fk.Properties.Select(p => p.Name))}) is {fk.DeleteBehavior}");

    [Fact]
    public void Every_foreign_key_to_users_cascades_on_delete()
    {
        var options = new DbContextOptionsBuilder<CampusPulseDbContext>();
        DependencyInjection.Configure(options, null);
        using var db = new CampusPulseDbContext(options.Options);

        var offenders = ForeignKeysToUsersThatDontCascade(db.Model).ToList();

        Assert.True(offenders.Count == 0,
            "Per-user tables must refer to users with .OnDelete(DeleteBehavior.Cascade), or DELETE /me leaves "
            + "their rows behind: " + string.Join("; ", offenders));
    }

    /// <summary>The check itself, on a model that has one table of each kind.</summary>
    [Fact]
    public void The_check_finds_a_foreign_key_that_does_not_cascade()
    {
        var options = new DbContextOptionsBuilder<ProbeContext>();
        options.UseNpgsql();
        using var db = new ProbeContext(options.Options);

        var offenders = ForeignKeysToUsersThatDontCascade(db.Model).ToList();

        var offender = Assert.Single(offenders);
        Assert.StartsWith("restricted_rows ", offender);
    }

    private sealed class CascadingRow
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }
    }

    private sealed class RestrictedRow
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }
    }

    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>().ToTable("users").HasKey(u => u.Id);
            modelBuilder.Entity<CascadingRow>(row =>
            {
                row.ToTable("cascading_rows");
                row.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<RestrictedRow>(row =>
            {
                row.ToTable("restricted_rows");
                row.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
            });
        }
    }
}
