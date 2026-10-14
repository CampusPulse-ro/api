using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CampusPulse.Api.Users;
using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CampusPulse.Api.Tests;

/// <summary>Sign-in configured, real Postgres: DELETE /me removes the student and leaves only a count.
/// The tests share one database, so they compare the total count before and after, and a test that
/// needs a day's count to itself runs on a fixed clock at a day nobody else uses.</summary>
public sealed class DeleteMeTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    private static string NewOid() => "oid-" + Guid.NewGuid().ToString("N");

    private static HttpRequestMessage Request(HttpMethod method, string oid, string? name = "Ana Pop")
    {
        var request = new HttpRequestMessage(method, "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(oid: oid, name: name));
        return request;
    }

    /// <summary>What a client does after an interactive sign-in.</summary>
    private static async Task<MeResponse> SignIn(HttpClient client, string oid)
    {
        var response = await client.SendAsync(Request(HttpMethod.Post, oid));
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"POST /me: {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<MeResponse>())!;
    }

    private async Task<int> TotalDeletions()
    {
        await using var db = factory.CreateDbContext();
        return await db.AccountDeletionCounts.SumAsync(c => c.Deletions);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The app on this database with extra services. With <paramref name="now"/>, the app's
    /// clock is fixed there; tokens are still checked against the real one.</summary>
    private WebApplicationFactory<Program> With(DateTimeOffset? now = null, ILoggerProvider? logs = null,
        IInterceptor? interceptor = null) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            if (now is { } fixedNow)
            {
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(fixedNow));
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                    options => options.TimeProvider = TimeProvider.System);
            }

            if (logs is not null)
            {
                services.AddSingleton<ILoggerProvider>(logs);
            }

            if (interceptor is not null)
            {
                services.ConfigureDbContext<CampusPulseDbContext>(options => options.AddInterceptors(interceptor));
            }
        }));

    [PostgresFact]
    public async Task Deleting_removes_the_student_and_counts_one_deletion()
    {
        var client = factory.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        var before = await TotalDeletions();

        var response = await client.SendAsync(Request(HttpMethod.Delete, oid));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before + 1, await TotalDeletions());
        await using var db = factory.CreateDbContext();
        Assert.False(await db.Users.AnyAsync(u => u.EntraObjectId == oid));
    }

    [PostgresFact]
    public async Task The_count_is_for_the_utc_day_of_the_deletion()
    {
        var day = new DateOnly(2001, 2, 3);
        using var late = With(now: new DateTimeOffset(2001, 2, 3, 23, 59, 0, TimeSpan.Zero));
        var client = late.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Delete, oid))).StatusCode);

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, (await db.AccountDeletionCounts.SingleAsync(c => c.Day == day)).Deletions);
    }

    [PostgresFact]
    public async Task A_second_delete_is_still_204_and_counts_nothing()
    {
        var client = factory.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Delete, oid))).StatusCode);
        var afterFirst = await TotalDeletions();

        var again = await client.SendAsync(Request(HttpMethod.Delete, oid));

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(afterFirst, await TotalDeletions());
    }

    [PostgresFact]
    public async Task Deleting_a_student_who_never_signed_in_is_204_and_counts_nothing()
    {
        var before = await TotalDeletions();

        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Delete, NewOid()));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before, await TotalDeletions());
    }

    /// <summary>Another device still holding tokens reads the account as gone and can't re-create it
    /// by reading; only a new interactive sign-in (POST) starts a fresh one.</summary>
    [PostgresFact]
    public async Task After_deleting_get_is_404_and_signing_in_again_starts_a_new_student()
    {
        var client = factory.CreateClient();
        var oid = NewOid();
        var first = await SignIn(client, oid);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Delete, oid))).StatusCode);

        var read = await client.SendAsync(Request(HttpMethod.Get, oid));
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        await using (var db = factory.CreateDbContext())
        {
            Assert.False(await db.Users.AnyAsync(u => u.EntraObjectId == oid));
        }

        var again = await client.SendAsync(Request(HttpMethod.Post, oid));

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.NotEqual(first.Id, (await again.Content.ReadFromJsonAsync<MeResponse>())!.Id);
    }

    [PostgresFact]
    public async Task Concurrent_deletes_of_one_student_count_once()
    {
        var client = factory.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        var before = await TotalDeletions();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.SendAsync(Request(HttpMethod.Delete, oid))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(before + 1, await TotalDeletions());
    }

    [PostgresFact]
    public async Task Concurrent_deletes_of_different_students_all_count()
    {
        var client = factory.CreateClient();
        var oids = Enumerable.Range(0, 10).Select(_ => NewOid()).ToList();
        foreach (var oid in oids)
        {
            await SignIn(client, oid);
        }

        var before = await TotalDeletions();

        var responses = await Task.WhenAll(oids.Select(oid => client.SendAsync(Request(HttpMethod.Delete, oid))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(before + oids.Count, await TotalDeletions());
    }

    /// <summary>The delete and the count are one transaction: when the count can't be written, the
    /// student is still there. A trigger refuses the count for one day, which only this test uses.</summary>
    [PostgresFact]
    public async Task When_the_count_fails_the_delete_is_rolled_back()
    {
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION refuse_test_count() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'count refused by the test'; END $$;
                CREATE TRIGGER refuse_test_count BEFORE INSERT OR UPDATE ON account_deletion_counts
                    FOR EACH ROW WHEN (NEW.day = DATE '1999-12-31') EXECUTE FUNCTION refuse_test_count();
                """);
        }

        try
        {
            using var refused = With(now: new DateTimeOffset(1999, 12, 31, 12, 0, 0, TimeSpan.Zero));
            var client = refused.CreateClient();
            var oid = NewOid();
            await SignIn(client, oid);
            var before = await TotalDeletions();

            var response = await client.SendAsync(Request(HttpMethod.Delete, oid));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await using var db = factory.CreateDbContext();
            Assert.True(await db.Users.AnyAsync(u => u.EntraObjectId == oid));
            Assert.Equal(before, await TotalDeletions());
        }
        finally
        {
            await using var db = factory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER refuse_test_count ON account_deletion_counts;
                DROP FUNCTION refuse_test_count();
                """);
        }
    }

    [PostgresFact]
    public async Task The_deletion_is_logged_once_and_no_log_line_names_the_student()
    {
        var logs = new CollectingLoggerProvider();
        using var logged = With(logs: logs);
        var client = logged.CreateClient();
        var oid = NewOid();
        var me = await SignIn(client, oid);

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Delete, oid))).StatusCode);

        var line = Assert.Single(logs.Entries, e => e.Category == MeEndpoint.LogCategory);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("Account deleted", line.Message);
        var identifiers = new[] { oid, me.Id.ToString(), me.Email };
        Assert.DoesNotContain(logs.Entries, e => identifiers.Any(id =>
            e.Message.Contains(id, StringComparison.OrdinalIgnoreCase)
            || e.ScopeValues.Any(kv => kv.Value?.ToString()?.Contains(id, StringComparison.OrdinalIgnoreCase) == true)));
    }

    /// <summary>Deletes the student's row, from another connection, just before this request saves
    /// its last-seen update: a DELETE /me landing between GET /me's read and its write.</summary>
    private sealed class DeleteBeforeUpdate(PostgresAppFactory factory, string oid) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<User>()
                    .Any(e => e.State == EntityState.Modified && e.Entity.EntraObjectId == oid))
            {
                Fired = true;
                await using var other = factory.CreateDbContext();
                await other.Users.Where(u => u.EntraObjectId == oid).ExecuteDeleteAsync(cancellationToken);
            }

            return result;
        }
    }

    [PostgresFact]
    public async Task A_get_overlapping_a_delete_is_404_and_does_not_recreate_the_student()
    {
        var oid = NewOid();
        await SignIn(factory.CreateClient(), oid);
        var interceptor = new DeleteBeforeUpdate(factory, oid);
        using var overlapping = With(interceptor: interceptor);

        // A changed name makes GET save, so the update meets the deleted row.
        var response = await overlapping.CreateClient().SendAsync(Request(HttpMethod.Get, oid, name: "Ana Pop-Ionescu"));

        Assert.True(interceptor.Fired);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var db = factory.CreateDbContext();
        Assert.False(await db.Users.AnyAsync(u => u.EntraObjectId == oid));
    }

    /// <summary>Makes POST /me's insert collide with a rival row (another first sign-in), then deletes
    /// the rival before POST looks it up: a DELETE /me landing between the two.</summary>
    private sealed class CollideThenVanish(PostgresAppFactory factory, string oid) : SaveChangesInterceptor
    {
        public int Collisions { get; private set; }

        public bool Vanished { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Collisions == 0 && eventData.Context!.ChangeTracker.Entries<User>()
                    .Any(e => e.State == EntityState.Added && e.Entity.EntraObjectId == oid))
            {
                Collisions++;
                await using var other = factory.CreateDbContext();
                other.Users.Add(new User(oid, "rival@student.upt.ro", "Rival", DateTimeOffset.UtcNow));
                await other.SaveChangesAsync(cancellationToken);
            }

            return result;
        }

        public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Collisions == 1 && !Vanished)
            {
                Vanished = true;
                await using var other = factory.CreateDbContext();
                await other.Users.Where(u => u.EntraObjectId == oid).ExecuteDeleteAsync(cancellationToken);
            }
        }
    }

    [PostgresFact]
    public async Task A_post_whose_rival_row_vanished_inserts_once_more()
    {
        var oid = NewOid();
        var interceptor = new CollideThenVanish(factory, oid);
        using var racing = With(interceptor: interceptor);

        var response = await racing.CreateClient().SendAsync(Request(HttpMethod.Post, oid));

        Assert.Equal(1, interceptor.Collisions);
        Assert.True(interceptor.Vanished);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var me = (await response.Content.ReadFromJsonAsync<MeResponse>())!;
        await using var db = factory.CreateDbContext();
        var stored = await db.Users.SingleAsync(u => u.EntraObjectId == oid);
        Assert.Equal(me.Id, stored.Id);
        Assert.Equal("ana@student.upt.ro", stored.Email);
    }
}
