using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampusPulse.Api.Timetable;
using CampusPulse.Api.Timetable.Import;
using CampusPulse.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CampusPulse.Api.Tests;

/// <summary>
/// The timetable cache, on the invented timetable (<see cref="ImportedTimetable"/>), with the SQL commands the app sends
/// counted: once loaded, the shared endpoints send none and <c>GET /me/timetable</c> sends one (the student's own
/// choices). The version check runs every 60 s here, on a hand-moved clock. Each import goes into an academic year of
/// its own, since the tests share the database.
/// </summary>
public sealed class TimetableCacheTests(ImportedTimetable timetable) : IClassFixture<ImportedTimetable>
{
    /// <summary>Monday of week 2, 12:00 in Romania.</summary>
    private static readonly DateTimeOffset Now = new(2030, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Counts the SQL commands the app sends.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private WebApplicationFactory<Program> App(TestClock clock, CommandCounter commands, ILoggerProvider? logs = null) =>
        timetable.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TimetableCache:VersionCheckSeconds", "60");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options => options.TimeProvider = TimeProvider.System);
                services.ConfigureDbContext<CampusPulseDbContext>(options => options.AddInterceptors(commands));
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }
            });
        });

    private static string NewOid() => "oid-" + Guid.NewGuid().ToString("N");

    private static HttpRequestMessage Request(HttpMethod method, string path, string oid, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(oid: oid));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return request;
    }

    private static async Task<T> Get<T>(HttpClient client, string path, string oid)
    {
        var response = await client.SendAsync(Request(HttpMethod.Get, path, oid));
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task<StudyGroupResponse> Group(HttpClient client, string programme, string series, string label) =>
        (await Get<List<ProgrammeResponse>>(client, "/timetable/programmes", NewOid()))
            .Single(p => p.Code == programme).Years.Single(y => y.Year == 1).Groups.Single(g => g.Series == series && g.Label == label);

    private static int Loads(CollectingLoggerProvider logs) =>
        logs.Entries.Count(e => e.Category == typeof(TimetableCache).FullName && e.Message.StartsWith("Timetable cache loaded", StringComparison.Ordinal));

    /// <summary>Imports the test workbook into another academic year, with programme ALFA renamed.</summary>
    private async Task ImportAnotherYear(string academicYear, string firstWeek, string alfaName)
    {
        var yaml = TimetableParserTests.Overrides
            .Replace("academicYear: \"2030-2031\"", $"academicYear: \"{academicYear}\"")
            .Replace("firstWeekStartsOn: \"2030-09-30\"", $"firstWeekStartsOn: \"{firstWeek}\"")
            .Replace("ALFA: \"Programul Alfa\"", $"ALFA: \"{alfaName}\"");
        var overrides = ImportOverrides.Parse(yaml);
        using var workbook = TimetableParserTests.Workbook();
        var bytes = workbook.Bytes();
        using var stream = new MemoryStream(bytes);
        var file = new ImportFile("orar", "orar.xlsx", ImportTimetableCommand.Sha256(bytes),
            new SheetParser(overrides).Parse("orar", "orar.xlsx", SheetGrid.Load(stream)));
        await using var db = timetable.Factory.CreateDbContext();
        var exit = await new TimetableImporter(db, TimeProvider.System, TextWriter.Null)
            .RunAsync(overrides, academicYear.PadRight(64, '0')[..64], [file], dryRun: false, force: false, CancellationToken.None);
        Assert.Equal(ImportExit.Done, exit);
    }

    private async Task<Guid> AGroupOf(string academicYear)
    {
        await using var db = timetable.Factory.CreateDbContext();
        var semester = await db.Semesters.SingleAsync(s => s.AcademicYear == academicYear);
        return (await db.StudyGroups.FirstAsync(g => g.SemesterId == semester.Id)).Id;
    }

    [PostgresFact]
    public async Task Once_loaded_shared_reads_send_no_SQL_and_a_students_timetable_one_command()
    {
        var commands = new CommandCounter();
        using var app = App(new TestClock(Now), commands);
        var client = app.CreateClient();
        var group = await Group(client, "ALFA", "X", "1.2");
        var oid = NewOid();
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Request(HttpMethod.Post, "/me", oid))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put, "/me/settings", oid, new { studyGroupId = group.Id }))).StatusCode);

        commands.Reset();
        await Get<List<ProgrammeResponse>>(client, "/timetable/programmes", oid);
        await Get<SemesterResponse>(client, "/semesters/current", oid);
        var groupWeek = await Get<TimetableResponse>(client, $"/timetable/groups/{group.Id}?from=2030-09-30&to=2030-10-06", oid);
        await Get<List<ChoiceSubjectResponse>>(client, $"/timetable/groups/{group.Id}/choices", oid);
        await Get<List<RoomResponse>>(client, "/rooms", oid);
        var room = await Get<RoomTimetableResponse>(client, "/rooms/K210/timetable?date=2030-09-30", oid);
        var shared = commands.Count;

        commands.Reset();
        var mine = await Get<TimetableResponse>(client, "/me/timetable?from=2030-09-30&to=2030-10-06", oid);

        Assert.Equal(0, shared);
        Assert.Equal(1, commands.Count);
        Assert.NotEmpty(groupWeek.Occurrences);
        Assert.Single(room.Occurrences);
        Assert.Equal(group.Id, mine.StudyGroup?.Id);
        Assert.Contains(mine.Occurrences, o => o.Subject == "Geometria fractalilor");
    }

    /// <summary>Twenty requests at once on a new instance: one of them loads the timetable, the others wait for it
    /// (no stampede on the database).</summary>
    [PostgresFact]
    public async Task Concurrent_first_reads_load_the_timetable_once()
    {
        var logs = new CollectingLoggerProvider();
        var commands = new CommandCounter();
        using var app = App(new TestClock(Now), commands, logs);
        var client = app.CreateClient();
        var oid = NewOid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.SendAsync(Request(HttpMethod.Get, "/timetable/programmes", oid))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, Loads(logs));
        // The version, then the timetable (nine queries, the semesters' calendars split in four).
        Assert.InRange(commands.Count, 2, 15);
    }

    [PostgresFact]
    public async Task After_the_interval_one_small_query_checks_for_a_new_import()
    {
        var logs = new CollectingLoggerProvider();
        var commands = new CommandCounter();
        var clock = new TestClock(Now);
        using var app = App(clock, commands, logs);
        var client = app.CreateClient();
        var oid = NewOid();
        await Get<List<RoomResponse>>(client, "/rooms", oid);

        commands.Reset();
        clock.Advance(TimeSpan.FromSeconds(59));
        await Get<List<RoomResponse>>(client, "/rooms", oid);
        var before = commands.Count;
        clock.Advance(TimeSpan.FromSeconds(2));
        await Get<List<RoomResponse>>(client, "/rooms", oid);
        await Get<List<RoomResponse>>(client, "/rooms", oid);

        Assert.Equal(0, before);
        Assert.Equal(1, commands.Count);
        Assert.Equal(1, Loads(logs));
    }

    /// <summary>An import elsewhere (the import is its own process) is served once the interval has passed; until
    /// then, changed data comes from memory.</summary>
    [PostgresFact]
    public async Task An_import_reloads_the_timetable_at_the_next_check()
    {
        var logs = new CollectingLoggerProvider();
        var clock = new TestClock(Now);
        using var app = App(clock, new CommandCounter(), logs);
        var client = app.CreateClient();
        var oid = NewOid();
        var before = (await Get<List<ProgrammeResponse>>(client, "/timetable/programmes", oid)).Single(p => p.Code == "ALFA").Name;
        var renamed = "Programul Alfa " + Guid.NewGuid().ToString("N")[..6];

        await ImportAnotherYear("2044-2045", "2044-09-26", renamed);
        var stillCached = (await Get<List<ProgrammeResponse>>(client, "/timetable/programmes", oid)).Single(p => p.Code == "ALFA").Name;
        clock.Advance(TimeSpan.FromSeconds(61));
        var reloaded = (await Get<List<ProgrammeResponse>>(client, "/timetable/programmes", oid)).Single(p => p.Code == "ALFA").Name;

        Assert.Equal(before, stillCached);
        Assert.Equal(renamed, reloaded);
        Assert.Equal(2, Loads(logs));
    }

    /// <summary>Something new is never missed: an id the cached timetable doesn't have (a group from an import that ran
    /// a moment ago, picked on another instance) makes the cache check at once.</summary>
    [PostgresFact]
    public async Task A_group_the_cache_does_not_have_yet_is_found_at_once()
    {
        var logs = new CollectingLoggerProvider();
        using var app = App(new TestClock(Now), new CommandCounter(), logs);
        var client = app.CreateClient();
        var oid = NewOid();
        await Get<List<RoomResponse>>(client, "/rooms", oid);

        await ImportAnotherYear("2045-2046", "2045-09-25", "Programul Alfa");
        var newGroup = await AGroupOf("2045-2046");
        var response = await client.SendAsync(Request(HttpMethod.Get, $"/timetable/groups/{newGroup}", oid));
        var unknown = await client.SendAsync(Request(HttpMethod.Get, $"/timetable/groups/{Guid.NewGuid()}", oid));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(2, Loads(logs));
    }
}
