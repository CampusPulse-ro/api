using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampusPulse.Api.Http;
using CampusPulse.Api.Timetable;

namespace CampusPulse.Api.Tests;

/// <summary>Conditional GETs (ETag, 304) and Cache-Control on the timetable and the student's own data, over the
/// invented timetable (<see cref="ImportedTimetable"/>).</summary>
public sealed class HttpCachingTests(ImportedTimetable timetable) : IClassFixture<ImportedTimetable>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static string NewOid() => "oid-" + Guid.NewGuid().ToString("N");

    private static HttpRequestMessage Request(HttpMethod method, string path, string oid, object? body = null, EntityTagHeaderValue? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(oid: oid));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.IfNoneMatch.Add(ifNoneMatch);
        }

        return request;
    }

    private async Task<(HttpClient Client, string Oid, StudyGroupResponse Group)> Student()
    {
        var client = timetable.Factory.CreateClient();
        var oid = NewOid();
        var programmes = await (await client.SendAsync(Request(HttpMethod.Get, "/timetable/programmes", oid))).Content.ReadFromJsonAsync<List<ProgrammeResponse>>(Json);
        var group = programmes!.Single(p => p.Code == "ALFA").Years.Single().Groups.Single(g => g.Series == "X" && g.Label == "1.2");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Request(HttpMethod.Post, "/me", oid))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put, "/me/settings", oid, new { studyGroupId = group.Id }))).StatusCode);
        return (client, oid, group);
    }

    /// <summary>Shared answers: a browser may keep those not tied to today for 60 s; those that depend on today's date
    /// (the current semester, a timetable without dates) it asks again each time, so it never shows yesterday.</summary>
    [PostgresFact]
    public async Task Shared_timetable_reads_carry_a_content_ETag_and_answer_304_when_unchanged()
    {
        var (client, oid, group) = await Student();

        foreach (var (path, today) in new[]
        {
            ("/timetable/programmes", true), ("/semesters/current", true), ($"/timetable/groups/{group.Id}", true),
            ($"/timetable/groups/{group.Id}?from=2030-09-30&to=2030-10-06", false), ($"/timetable/groups/{group.Id}/choices", false),
            ("/rooms", false), ("/rooms/K210/timetable", true), ("/rooms/K210/timetable?date=2030-09-30", false),
        })
        {
            var first = await client.SendAsync(Request(HttpMethod.Get, path, oid));
            var body = await first.Content.ReadAsByteArrayAsync();
            var etag = first.Headers.ETag;

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.NotNull(etag);
            Assert.True(etag.IsWeak, path);
            Assert.Equal(HttpCaching.ETagOf(body).ToString(), etag.ToString());   // the tag is the content's
            Assert.True(first.Headers.CacheControl is { Private: true, NoStore: false }, $"{path}: {first.Headers.CacheControl}");
            Assert.Equal((today, today ? null : TimeSpan.FromSeconds(60)), (first.Headers.CacheControl!.NoCache, first.Headers.CacheControl.MaxAge));

            var unchanged = await client.SendAsync(Request(HttpMethod.Get, path, oid, ifNoneMatch: etag));
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
            Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
            Assert.Equal(etag, unchanged.Headers.ETag);

            var stale = await client.SendAsync(Request(HttpMethod.Get, path, oid, ifNoneMatch: new EntityTagHeaderValue("\"0123\"", isWeak: true)));
            Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        }
    }

    /// <summary>The student's own data: never stored by a browser (no-store: a shared lab PC keeps nothing after
    /// sign-out), but tagged, so a client that keeps its own copy gets a 304 when nothing changed, and a new tag when it
    /// did.</summary>
    [PostgresFact]
    public async Task A_students_own_reads_are_revalidated_and_change_with_their_data()
    {
        var (client, oid, group) = await Student();
        const string Week = "/me/timetable?from=2030-09-30&to=2030-10-06";

        foreach (var path in new[] { "/me", "/me/settings", "/me/subjects", "/me/reminders", Week })
        {
            var response = await client.SendAsync(Request(HttpMethod.Get, path, oid));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl is { Private: true, NoStore: true, MaxAge: null }, $"{path}: {response.Headers.CacheControl}");
            Assert.NotNull(response.Headers.ETag);
        }

        var before = (await client.SendAsync(Request(HttpMethod.Get, Week, oid))).Headers.ETag;
        var unchanged = await client.SendAsync(Request(HttpMethod.Get, Week, oid, ifNoneMatch: before));
        var choice = (await (await client.SendAsync(Request(HttpMethod.Get, $"/timetable/groups/{group.Id}/choices", oid)))
            .Content.ReadFromJsonAsync<List<ChoiceSubjectResponse>>(Json))!.Single();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put, $"/me/subjects/{choice.Id}", oid,
            new { offeredSlotId = choice.Offers[0].Id }))).StatusCode);
        var changed = await client.SendAsync(Request(HttpMethod.Get, Week, oid, ifNoneMatch: before));

        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(before, changed.Headers.ETag);
        Assert.Contains((await changed.Content.ReadFromJsonAsync<TimetableResponse>(Json))!.Occurrences, o => o.Subject == "Pedagogia jocului");
    }

    [PostgresFact]
    public async Task Writes_and_errors_are_never_stored()
    {
        var (client, oid, group) = await Student();

        var write = await client.SendAsync(Request(HttpMethod.Put, "/me/settings", oid, new { studyGroupId = group.Id }));
        var noAccount = await client.SendAsync(Request(HttpMethod.Get, "/me/timetable", NewOid()));
        var badRange = await client.SendAsync(Request(HttpMethod.Get, "/me/timetable?from=2030-10-10&to=2030-10-01", oid));

        Assert.All(new[] { write, noAccount, badRange }, response =>
        {
            Assert.True(response.Headers.CacheControl?.NoStore, $"{(int)response.StatusCode}: {response.Headers.CacheControl}");
            Assert.Null(response.Headers.ETag);
        });
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.BadRequest), (write.StatusCode, noAccount.StatusCode, badRange.StatusCode));
    }
}
