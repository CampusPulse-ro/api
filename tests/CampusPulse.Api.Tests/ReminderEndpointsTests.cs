using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CampusPulse.Api.Reminders;
using CampusPulse.Api.Timetable;
using CampusPulse.Domain.Reminders;
using CampusPulse.Domain.Timetable;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static CampusPulse.Api.Tests.PushTesting;

namespace CampusPulse.Api.Tests;

/// <summary>The rules of class reminders that need no database: when one is due, Romanian time, and what it says.</summary>
public sealed class ClassReminderRulesTests
{
    private static readonly DateTimeOffset Start = new(2030, 11, 25, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lead = TimeSpan.FromMinutes(15);

    private static DateTimeOffset At(int hour, int minute, int second = 0) => new(2030, 11, 25, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void Due_in_the_tick_whose_window_holds_start_minus_lead()
    {
        // Due at 05:45: in (05:44, 05:45] and (05:44:30, 05:45:30], not in the windows before or after.
        Assert.True(ClassReminderSender.IsDue(Start, Lead, At(5, 44), At(5, 45)));
        Assert.True(ClassReminderSender.IsDue(Start, Lead, At(5, 44, 30), At(5, 45, 30)));
        Assert.False(ClassReminderSender.IsDue(Start, Lead, At(5, 43), At(5, 44, 59)));
        Assert.False(ClassReminderSender.IsDue(Start, Lead, At(5, 45), At(5, 46)));
    }

    [Fact]
    public void A_late_tick_still_sends_before_the_class_but_never_once_it_started()
    {
        // A window that opened before the due time (a skipped tick, a restart) sends late, while the class hasn't begun.
        Assert.True(ClassReminderSender.IsDue(Start, Lead, At(5, 0), At(5, 59, 59)));
        Assert.False(ClassReminderSender.IsDue(Start, Lead, At(5, 0), At(6, 0)));
        Assert.False(ClassReminderSender.IsDue(Start, Lead, At(5, 0), At(6, 30)));
    }

    [Fact]
    public void Romanian_times_follow_summer_and_winter_time()
    {
        Assert.Equal(new DateTimeOffset(2030, 10, 7, 5, 0, 0, TimeSpan.Zero), RomanianTime.Instant(new DateOnly(2030, 10, 7), new TimeOnly(8, 0)));
        Assert.Equal(new DateTimeOffset(2030, 11, 25, 6, 0, 0, TimeSpan.Zero), RomanianTime.Instant(new DateOnly(2030, 11, 25), new TimeOnly(8, 0)));
        Assert.Equal(new DateOnly(2030, 11, 26), RomanianTime.DateOf(new DateTimeOffset(2030, 11, 25, 22, 30, 0, TimeSpan.Zero)));
    }

    /// <summary>The hour skipped in March and the one repeated in October never throw; no class is ever at 03:00.</summary>
    [Fact]
    public void Times_the_clocks_skip_or_repeat_still_have_an_instant()
    {
        Assert.Equal(new DateTimeOffset(2031, 3, 30, 1, 30, 0, TimeSpan.Zero), RomanianTime.Instant(new DateOnly(2031, 3, 30), new TimeOnly(3, 30)));
        Assert.Equal(new DateTimeOffset(2030, 10, 27, 1, 30, 0, TimeSpan.Zero), RomanianTime.Instant(new DateOnly(2030, 10, 27), new TimeOnly(3, 30)));
    }

    private static OccurrenceResponse Occurrence(string? shortName, ActivityKind[] kinds, string? room) => new(
        new DateOnly(2030, 11, 25), 9, new TimeOnly(8, 0), new TimeOnly(10, 0), Guid.Parse("0193b3c4-0000-7000-8000-000000000001"),
        "Mobile Systems and Applications", shortName, null, kinds, room, null, null, false, null, null);

    [Fact]
    public void The_text_is_the_short_name_and_the_minutes_left_then_the_kinds_and_the_room()
    {
        Assert.Equal(("MSA in 15 min", "Lab · 303CP"), ClassReminderSender.Text(Occurrence("MSA", [ActivityKind.Lab], "303CP"), 15));
        Assert.Equal(("Mobile Systems and Applications in 5 min", "Lab + Project"),
            ClassReminderSender.Text(Occurrence(null, [ActivityKind.Lab, ActivityKind.Project], null), 5));
        Assert.Equal(("MSA in 60 min", "AULA"), ClassReminderSender.Text(Occurrence("MSA", [], "AULA"), 60));
        Assert.Equal(("MSA in 10 min", "Mobile Systems and Applications"), ClassReminderSender.Text(Occurrence("MSA", [], null), 10));
    }

    [Fact]
    public void The_data_names_the_class_as_the_timetable_does()
    {
        var data = ClassReminderSender.Data(Occurrence("MSA", [ActivityKind.Lab], "303CP"));

        Assert.Equal(new Dictionary<string, string>
        {
            ["type"] = "classReminder",
            ["date"] = "2030-11-25",
            ["startsAt"] = "08:00:00",
            ["subjectId"] = "0193b3c4-0000-7000-8000-000000000001",
        }, data);
    }

    [Fact]
    public void The_look_back_is_the_longest_lead()
    {
        Assert.Equal(TimeSpan.FromMinutes(ReminderSettings.MaxMinutesBefore), ClassReminderSender.LookBack);
    }

    private sealed class DefaultFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres",
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
        }
    }

    /// <summary>On unless configured off; the shared test factories turn it off so no tick runs behind a test.</summary>
    [Fact]
    public void Reminders_are_on_by_default_and_the_test_factories_turn_them_off()
    {
        using var defaults = new DefaultFactory();
        using var tests = new CampusPulseAppFactory();

        Assert.True(defaults.Services.GetRequiredService<IOptions<ReminderOptions>>().Value.Enabled);
        Assert.False(tests.Services.GetRequiredService<IOptions<ReminderOptions>>().Value.Enabled);
        Assert.Contains(defaults.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is ClassReminderSender);
    }
}

public sealed class ReminderEndpointsTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    private static HttpRequestMessage Put(string oid, string json)
    {
        var request = Request(HttpMethod.Put, "/me/reminders", oid);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<int?> MinutesBefore(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var minutes = body.GetProperty("minutesBefore");
        return minutes.ValueKind == JsonValueKind.Null ? null : minutes.GetInt32();
    }

    [PostgresFact]
    public async Task Reminders_are_off_until_set_and_take_each_allowed_lead_and_null()
    {
        var client = factory.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);

        Assert.Null(await MinutesBefore(await client.SendAsync(Request(HttpMethod.Get, "/me/reminders", oid))));

        foreach (var lead in new[] { 5, 10, 15, 30, 60 })
        {
            Assert.Equal(lead, await MinutesBefore(await client.SendAsync(Put(oid, $$"""{ "minutesBefore": {{lead}} }"""))));
            Assert.Equal(lead, await MinutesBefore(await client.SendAsync(Request(HttpMethod.Get, "/me/reminders", oid))));
        }

        Assert.Null(await MinutesBefore(await client.SendAsync(Put(oid, """{ "minutesBefore": null }"""))));
        Assert.Null(await MinutesBefore(await client.SendAsync(Request(HttpMethod.Get, "/me/reminders", oid))));
        await using var db = factory.CreateDbContext();
        Assert.False(await db.ReminderSettings.AnyAsync(r => db.Users.Any(u => u.Id == r.UserId && u.EntraObjectId == oid)));
    }

    [PostgresTheory]
    [InlineData("""{ "minutesBefore": 0 }""")]
    [InlineData("""{ "minutesBefore": 7 }""")]
    [InlineData("""{ "minutesBefore": 45 }""")]
    [InlineData("""{ "minutesBefore": 61 }""")]
    [InlineData("""{ "minutesBefore": -15 }""")]
    [InlineData("""{ "minutesBefore": 15.5 }""")]
    [InlineData("""{ "minutesBefore": "15" }""")]
    [InlineData("""{ }""")]
    [InlineData("""15""")]
    public async Task Anything_else_is_a_400_problem_and_changes_nothing(string json)
    {
        var client = factory.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        await client.SendAsync(Put(oid, """{ "minutesBefore": 30 }"""));

        var response = await client.SendAsync(Put(oid, json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(30, await MinutesBefore(await client.SendAsync(Request(HttpMethod.Get, "/me/reminders", oid))));
    }

    [PostgresFact]
    public async Task Without_an_account_it_is_404_and_without_sign_in_401()
    {
        var client = factory.CreateClient();
        var oid = NewOid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Get, "/me/reminders", oid))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Put(oid, """{ "minutesBefore": 15 }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Put(oid, """{ "minutesBefore": null }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me/reminders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PutAsync("/me/reminders", new StringContent("""{ "minutesBefore": 15 }""", Encoding.UTF8, "application/json"))).StatusCode);
    }

    [PostgresFact]
    public async Task Deleting_the_account_deletes_the_setting()
    {
        var client = factory.CreateClient();
        var oid = NewOid();
        var userId = await SignIn(client, oid);
        await client.SendAsync(Put(oid, """{ "minutesBefore": 10 }"""));

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Delete, "/me", oid))).StatusCode);

        await using var db = factory.CreateDbContext();
        Assert.False(await db.ReminderSettings.AnyAsync(r => r.UserId == userId));
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Get, "/me/reminders", oid))).StatusCode);
    }
}
