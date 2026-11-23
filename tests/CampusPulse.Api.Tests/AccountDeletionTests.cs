using System.Net;
using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CampusPulse.Api.Tests;

/// <summary><c>DELETE /me</c> on an account with everything of the accounts feature: identities, sessions, deadlines,
/// and the sign-in codes of its addresses.</summary>
public sealed class AccountDeletionTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    [PostgresFact]
    public async Task Deleting_the_account_deletes_its_identities_sessions_deadlines_and_codes()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewEmail();
        var oid = "oid-" + Guid.NewGuid().ToString("N");
        var account = await app.SignInByEmail(email);
        await app.Post("/me/identities/upt-microsoft", new { accessToken = TestAuth.Token(oid: oid) }, account.AccessToken);
        await app.Post("/me/deadlines", new { title = "Lab", dueAt = DateTimeOffset.UtcNow.AddDays(3) }, account.AccessToken);
        await app.StartEmail(email);
        await app.StartEmail($"{oid}@student.upt.ro");
        var other = AccountsApp.NewEmail();
        await app.StartEmail(other);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Send(HttpMethod.Delete, "/me", bearer: account.AccessToken)).StatusCode);

        await using var db = factory.CreateDbContext();
        var id = account.User.Id;
        Assert.False(await db.Users.AnyAsync(u => u.Id == id));
        Assert.False(await db.UserIdentities.AnyAsync(i => i.UserId == id));
        Assert.False(await db.RefreshTokens.AnyAsync(t => t.UserId == id));
        Assert.False(await db.Deadlines.AnyAsync(d => d.UserId == id));
        Assert.False(await db.EmailSignInCodes.AnyAsync(c => c.Email == email || c.Email == $"{oid}@student.upt.ro"));
        Assert.True(await db.EmailSignInCodes.AnyAsync(c => c.Email == other));   // someone else's address stays
    }

    /// <summary>A UPT account whose address another account has keeps it only on its identity: deleting it leaves that
    /// address's code row (the other account's live code and limits) alone.</summary>
    [PostgresFact]
    public async Task Deleting_an_account_keeps_the_codes_of_an_address_another_account_has()
    {
        using var app = new AccountsApp(factory);
        var shared = AccountsApp.NewUptEmail();
        var owner = await app.SignInByEmail(shared);
        await app.Post("/me/identities/upt-microsoft", new { accessToken = TestAuth.Token(oid: "oid-" + Guid.NewGuid().ToString("N")) },
            owner.AccessToken);
        var other = await app.SignInWithUpt("oid-" + Guid.NewGuid().ToString("N"), shared);
        Assert.Equal((true, (string?)null), (other.IsNew, other.User.Email));
        await app.StartEmail(shared);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Send(HttpMethod.Delete, "/me", bearer: other.AccessToken)).StatusCode);

        Assert.Equal(owner.User.Id, (await AccountsApp.Tokens(await app.VerifyEmail(shared, app.Email.CodeFor(shared)))).User.Id);
    }

    [PostgresFact]
    public async Task The_cleanup_deletes_expired_sessions_and_old_code_rows()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewEmail();
        var account = await app.SignInByEmail(email);
        app.Clock.Advance(TimeSpan.FromDays(61));

        var (tokens, codes) = await app.App.Services.GetRequiredService<CampusPulse.Api.Accounts.SignInCleanup>().RunOnceAsync(default);

        Assert.True(tokens >= 1 && codes >= 1, $"{tokens} tokens, {codes} codes");
        await using var db = factory.CreateDbContext();
        Assert.False(await db.RefreshTokens.AnyAsync(t => t.UserId == account.User.Id));
        Assert.False(await db.EmailSignInCodes.AnyAsync(c => c.Email == email));
    }
}

/// <summary>The accounts migration on a database of the previous release: every account becomes a UPT account with its
/// identity, and its email (a UPT address) the account's verified one when no older account has it. The previous
/// release's own column is left as it was, so that release still works on the migrated schema.</summary>
public sealed class AccountsMigrationTests
{
    [PostgresFact]
    public async Task Every_existing_student_gets_a_UPT_identity_and_a_normalized_unique_email()
    {
        var server = PostgresFactAttribute.ServerOrNull()!;
        var connectionString = new NpgsqlConnectionStringBuilder(server)
        {
            Database = $"campuspulse_migration_{Guid.NewGuid():N}",
            CommandTimeout = 300,
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CampusPulseDbContext>();
        DependencyInjection.Configure(options, connectionString);
        await using var db = new CampusPulseDbContext(options.Options);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("AddImportRunVersionIndex");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO users (id, entra_object_id, email, display_name, created_at, last_seen_at) VALUES
                  ('01900000-0000-7000-8000-000000000001', 'oid-a', ' Ana@Student.UPT.ro ', 'Ana', now(), now()),
                  ('01900000-0000-7000-8000-000000000002', 'oid-b', 'ana@student.upt.ro', 'Ana again', now(), now()),
                  ('01900000-0000-7000-8000-000000000003', 'oid-c', '', 'No email', now(), now()),
                  ('01900000-0000-7000-8000-000000000004', 'oid-d', 'Dan@Gmail.com', 'Dan', now(), now());
                """);

            await db.Database.MigrateAsync();

            var users = await db.Users.OrderBy(u => u.Id).Select(u => new { u.EntraObjectId, u.Email, u.LegacyEmail }).ToListAsync();
            Assert.Equal(new (string?, string?)[] { ("oid-a", "ana@student.upt.ro"), ("oid-b", null), ("oid-c", null), ("oid-d", null) },
                users.Select(u => (u.EntraObjectId, u.Email)));
            Assert.Equal([" Ana@Student.UPT.ro ", "ana@student.upt.ro", "", "Dan@Gmail.com"], users.Select(u => u.LegacyEmail));
            var identities = await db.UserIdentities.OrderBy(i => i.Subject).ToListAsync();
            Assert.Equal(["oid-a", "oid-b", "oid-c", "oid-d"], identities.Select(i => i.Subject));
            Assert.All(identities, i => Assert.Equal(UserIdentity.UptMicrosoft, i.Provider));
            Assert.Equal(new string?[] { "ana@student.upt.ro", "ana@student.upt.ro", null, "dan@gmail.com" }, identities.Select(i => i.Email));

            // The previous release, rolled back to, adds students as it always did: an address another account has, or
            // none, is no conflict in its column.
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO users (id, entra_object_id, email, display_name, created_at, last_seen_at) VALUES
                  ('01900000-0000-7000-8000-000000000005', 'oid-e', 'ana@student.upt.ro', 'Ana', now(), now()),
                  ('01900000-0000-7000-8000-000000000006', 'oid-f', '', 'Nobody', now(), now()),
                  ('01900000-0000-7000-8000-000000000007', 'oid-g', '', 'Nobody else', now(), now());
                """);
            Assert.Equal(0, await db.Users.CountAsync(u => u.EntraObjectId != null && u.LegacyEmail == null));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await db.Database.EnsureDeletedAsync();
        }
    }
}
