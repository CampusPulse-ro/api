using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CampusPulse.Api.Users;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CampusPulse.Api.Tests;

/// <summary>Sign-in configured, real Postgres: DELETE /me removes the student and leaves only a count.
/// The tests share one database, so they compare the total count before and after.</summary>
public sealed class DeleteMeTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    private static string NewOid() => "oid-" + Guid.NewGuid().ToString("N");

    private static HttpRequestMessage Request(HttpMethod method, string oid)
    {
        var request = new HttpRequestMessage(method, "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(oid: oid));
        return request;
    }

    private async Task<MeResponse> SignIn(HttpClient client, string oid)
    {
        var response = await client.SendAsync(Request(HttpMethod.Get, oid));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MeResponse>())!;
    }

    private async Task<int> TotalDeletions()
    {
        await using var db = factory.CreateDbContext();
        return await db.AccountDeletionCounts.SumAsync(c => c.Deletions);
    }

    [PostgresFact]
    public async Task Deleting_removes_the_student_and_counts_one_deletion_for_the_day()
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
        // Counted on the UTC day it happened (yesterday only if the test straddled midnight).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.True(await db.AccountDeletionCounts.AnyAsync(c => c.Day == today || c.Day == today.AddDays(-1)));
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

    [PostgresFact]
    public async Task Signing_in_again_after_deleting_starts_a_new_student()
    {
        var client = factory.CreateClient();
        var oid = NewOid();
        var first = await SignIn(client, oid);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Delete, oid))).StatusCode);

        var second = await SignIn(client, oid);

        Assert.NotEqual(first.Id, second.Id);
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.EntraObjectId == oid));
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

    [PostgresFact]
    public async Task The_deletion_is_logged_once_and_no_log_line_names_the_student()
    {
        var logs = new CollectingLoggerProvider();
        using var logged = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(logs)));
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
}
