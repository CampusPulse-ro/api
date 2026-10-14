using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace CampusPulse.Api.Tests;

/// <summary>
/// DELETE /me deletes the <c>users</c> row and relies on the database to delete everything under it,
/// so every foreign key on a path from <c>users</c> (the tables that refer to it, the tables that
/// refer to those, and so on) must be ON DELETE CASCADE. Checked in the EF model here and in the
/// migrated database below. Today nothing refers to <c>users</c>; this fails the test run when a
/// later table (settings, device tokens, Campus Virtual tokens) forgets.
/// </summary>
public sealed class PerUserDataTests
{
    /// <summary>The tables reached from <c>users</c> by following foreign keys backwards, and the
    /// keys on those paths that would leave rows behind or block the delete.</summary>
    public sealed record CascadeWalk(IReadOnlyList<string> Reached, IReadOnlyList<string> Offenders);

    /// <summary>Only Cascade is a database-side ON DELETE CASCADE. ClientCascade and ClientSetNull
    /// (an optional key's default) act in EF's change tracker, which the delete doesn't use.</summary>
    public static CascadeWalk WalkFromUsers(IModel model)
    {
        var root = model.FindEntityType(typeof(User)) ?? throw new InvalidOperationException("User is not in the model.");
        var reached = new List<IEntityType> { root };
        var offenders = new List<string>();
        for (var i = 0; i < reached.Count; i++)
        {
            foreach (var fk in reached[i].GetReferencingForeignKeys())
            {
                if (fk.DeleteBehavior != DeleteBehavior.Cascade)
                {
                    offenders.Add($"{Table(fk.DeclaringEntityType)} ({string.Join(", ", fk.Properties.Select(p => p.Name))}) "
                        + $"-> {Table(fk.PrincipalEntityType)} is {fk.DeleteBehavior}");
                }

                if (!reached.Contains(fk.DeclaringEntityType))
                {
                    reached.Add(fk.DeclaringEntityType);
                }
            }
        }

        return new CascadeWalk(reached.Skip(1).Select(Table).ToList(), offenders);
    }

    private static string Table(IReadOnlyEntityType entity) => entity.GetTableName() ?? entity.DisplayName();

    [Fact]
    public void Every_foreign_key_under_users_cascades_on_delete()
    {
        var options = new DbContextOptionsBuilder<CampusPulseDbContext>();
        DependencyInjection.Configure(options, null);
        using var db = new CampusPulseDbContext(options.Options);

        var walk = WalkFromUsers(db.Model);

        Assert.True(walk.Offenders.Count == 0,
            "Every foreign key under users must be .OnDelete(DeleteBehavior.Cascade), or DELETE /me leaves "
            + "rows behind: " + string.Join("; ", walk.Offenders));
    }

    /// <summary>The walk itself, on a model with one table of each kind.</summary>
    [Fact]
    public void The_walk_finds_every_kind_of_key_that_does_not_cascade_at_any_depth()
    {
        var options = new DbContextOptionsBuilder<ProbeContext>();
        options.UseNpgsql();
        using var db = new ProbeContext(options.Options);

        var walk = WalkFromUsers(db.Model);

        // The join table is reached; groups is not (nothing about it is under users).
        Assert.Equal(
            ["cascading_rows", "client_cascade_rows", "device_tokens", "devices", "optional_rows", "restricted_rows", "user_groups"],
            walk.Reached.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["client_cascade_rows (UserId) -> users is ClientCascade",
             "device_tokens (DeviceId) -> devices is Restrict",
             "optional_rows (UserId) -> users is ClientSetNull",
             "restricted_rows (UserId) -> users is Restrict"],
            walk.Offenders.Order(StringComparer.Ordinal));
    }

    private sealed class Row
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }
    }

    private sealed class OptionalRow
    {
        public Guid Id { get; set; }

        public Guid? UserId { get; set; }
    }

    private sealed class Device
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }
    }

    private sealed class DeviceToken
    {
        public Guid Id { get; set; }

        public Guid DeviceId { get; set; }
    }

    private sealed class Group
    {
        public Guid Id { get; set; }

        public List<User> Members { get; } = [];
    }

    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>().ToTable("users").HasKey(u => u.Id);
            modelBuilder.SharedTypeEntity<Row>("cascading_rows", row =>
                row.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade));
            modelBuilder.SharedTypeEntity<Row>("restricted_rows", row =>
                row.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict));
            modelBuilder.SharedTypeEntity<Row>("client_cascade_rows", row =>
                row.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.ClientCascade));
            // An optional key with no OnDelete: EF's default for it is ClientSetNull.
            modelBuilder.Entity<OptionalRow>(row =>
            {
                row.ToTable("optional_rows");
                row.HasOne<User>().WithMany().HasForeignKey(r => r.UserId);
            });
            // Two levels down: devices cascade from users, but their tokens don't cascade from devices.
            modelBuilder.Entity<Device>(device =>
            {
                device.ToTable("devices");
                device.HasOne<User>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<DeviceToken>(token =>
            {
                token.ToTable("device_tokens");
                token.HasOne<Device>().WithMany().HasForeignKey(t => t.DeviceId).OnDelete(DeleteBehavior.Restrict);
            });
            // A many-to-many join table (its keys cascade by default).
            modelBuilder.Entity<Group>(group =>
            {
                group.ToTable("groups");
                group.HasMany(g => g.Members).WithMany().UsingEntity("user_groups");
            });
        }
    }
}

/// <summary>
/// The same walk over the migrated database's <c>pg_constraint</c>, so a foreign key written by hand
/// in a migration (<c>migrationBuilder.Sql</c>) is checked too: every key on a path from
/// <c>users</c> must have <c>confdeltype = 'c'</c> (ON DELETE CASCADE).
/// </summary>
public sealed class PerUserDataDatabaseTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    private sealed record ForeignKey(string Table, string Constraint, string OnDelete);

    /// <summary>Every foreign key on a path from <paramref name="root"/>, with its ON DELETE action
    /// (<c>c</c> cascade, <c>a</c> no action, <c>r</c> restrict, <c>n</c> set null, <c>d</c> set default).</summary>
    private static async Task<List<ForeignKey>> KeysUnder(NpgsqlConnection connection, string root, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("""
            WITH RECURSIVE reached(rel) AS (
                SELECT to_regclass(@root)::oid
                UNION
                SELECT c.conrelid FROM pg_constraint c JOIN reached r ON c.confrelid = r.rel WHERE c.contype = 'f'
            )
            SELECT c.conrelid::regclass::text, c.conname::text, c.confdeltype::text
            FROM pg_constraint c JOIN reached r ON c.confrelid = r.rel
            WHERE c.contype = 'f'
            ORDER BY 1, 2
            """, connection, transaction);
        command.Parameters.AddWithValue("root", root);
        var keys = new List<ForeignKey>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add(new ForeignKey(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return keys;
    }

    private async Task<NpgsqlConnection> Open()
    {
        var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    [PostgresFact]
    public async Task Every_foreign_key_under_users_in_the_database_is_on_delete_cascade()
    {
        await using var connection = await Open();

        var offenders = (await KeysUnder(connection, "users")).Where(k => k.OnDelete != "c").ToList();

        Assert.True(offenders.Count == 0, "Not ON DELETE CASCADE: "
            + string.Join("; ", offenders.Select(k => $"{k.Table}.{k.Constraint} ({k.OnDelete})")));
    }

    /// <summary>The query itself, on hand-made tables in a transaction that is rolled back.</summary>
    [PostgresFact]
    public async Task The_database_walk_finds_keys_that_do_not_cascade_two_levels_down()
    {
        await using var connection = await Open();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var create = new NpgsqlCommand("""
            CREATE TABLE probe_devices (id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE);
            CREATE TABLE probe_device_tokens (id uuid PRIMARY KEY, device_id uuid NOT NULL REFERENCES probe_devices (id));
            CREATE TABLE probe_restricted (id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES users (id) ON DELETE RESTRICT);
            """, connection, transaction))
        {
            await create.ExecuteNonQueryAsync();
        }

        var keys = await KeysUnder(connection, "users", transaction);
        await transaction.RollbackAsync();

        Assert.Equal(
            [("probe_device_tokens", "a"), ("probe_devices", "c"), ("probe_restricted", "r")],
            keys.Select(k => (k.Table, k.OnDelete)));
    }
}
