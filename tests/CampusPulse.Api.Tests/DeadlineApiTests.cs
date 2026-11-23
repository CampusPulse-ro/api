using System.Net;
using System.Net.Http.Json;
using CampusPulse.Api.Deadlines;
using CampusPulse.Domain.Deadlines;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Tests;

/// <summary>The student's own deadlines (<c>/me/deadlines</c>).</summary>
public sealed class DeadlineApiTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 30, 23, 59, 0, TimeSpan.FromHours(2));

    private static async Task<DeadlineResponse> Read(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DeadlineResponse>(AccountsApp.Json))!;
    }

    private static async Task<List<DeadlineResponse>> List(AccountsApp app, string token, string query = "") =>
        (await (await app.Send(HttpMethod.Get, "/me/deadlines" + query, bearer: token)).Content
            .ReadFromJsonAsync<List<DeadlineResponse>>(AccountsApp.Json))!;

    [PostgresFact]
    public async Task A_student_adds_edits_completes_and_deletes_a_deadline()
    {
        using var app = new AccountsApp(factory);
        var token = (await app.SignInByEmail(AccountsApp.NewEmail())).AccessToken;

        var created = await app.Post("/me/deadlines", new { title = " Lab 3 report ", course = "MSA", dueAt = Monday, notes = "PDF" }, token);
        var deadline = await Read(created);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/me/deadlines/{deadline.Id}", created.Headers.Location?.OriginalString);
        Assert.Equal(("Lab 3 report", "MSA", "PDF", false, Monday), (deadline.Title, deadline.Course, deadline.Notes, deadline.Done, deadline.DueAt));

        var done = await Read(await app.Send(HttpMethod.Patch, $"/me/deadlines/{deadline.Id}", new { done = true, course = "" }, token));
        Assert.Equal(("Lab 3 report", (string?)null, "PDF", true), (done.Title, done.Course, done.Notes, done.Done));
        var renamed = await Read(await app.Send(HttpMethod.Patch, $"/me/deadlines/{deadline.Id}", new { title = "Lab 3" }, token));
        Assert.Equal(("Lab 3", true, Monday), (renamed.Title, renamed.Done, renamed.DueAt));
        Assert.Equal(renamed, Assert.Single(await List(app, token)));

        Assert.Equal(HttpStatusCode.NoContent, (await app.Send(HttpMethod.Delete, $"/me/deadlines/{deadline.Id}", bearer: token)).StatusCode);
        Assert.Empty(await List(app, token));
        Assert.Equal(HttpStatusCode.NoContent, (await app.Send(HttpMethod.Delete, $"/me/deadlines/{deadline.Id}", bearer: token)).StatusCode);
    }

    [PostgresFact]
    public async Task Deadlines_come_by_due_time_and_from_to_filter_them()
    {
        using var app = new AccountsApp(factory);
        var token = (await app.SignInByEmail(AccountsApp.NewEmail())).AccessToken;
        foreach (var days in new[] { 3, 1, 2 })
        {
            await app.Post("/me/deadlines", new { title = $"in {days}", dueAt = Monday.AddDays(days) }, token);
        }

        Assert.Equal(["in 1", "in 2", "in 3"], (await List(app, token)).Select(d => d.Title));
        var from = Uri.EscapeDataString(Monday.AddDays(2).ToString("O"));
        Assert.Equal(["in 2", "in 3"], (await List(app, token, $"?from={from}")).Select(d => d.Title));
        Assert.Equal(["in 1", "in 2"], (await List(app, token, $"?to={from}")).Select(d => d.Title));
    }

    /// <summary>Values out of bounds are <c>invalid-deadline</c>; a body that doesn't match the contract (no
    /// <c>dueAt</c>, a null one, not a date) is <c>invalid-request</c>. Nothing is stored either way.</summary>
    [PostgresTheory]
    [InlineData("""{ "title": "", "dueAt": "2026-11-30T21:59:00Z" }""", "invalid-deadline")]
    [InlineData("""{ "title": "   ", "dueAt": "2026-11-30T21:59:00Z" }""", "invalid-deadline")]
    [InlineData("""{ "title": "x", "dueAt": "2026-11-30T21:59:00Z", "course": "{201}" }""", "invalid-deadline")]
    [InlineData("""{ "title": "x", "dueAt": "2026-11-30T21:59:00Z", "notes": "{2001}" }""", "invalid-deadline")]
    [InlineData("""{ "title": "{201}", "dueAt": "2026-11-30T21:59:00Z" }""", "invalid-deadline")]
    [InlineData("""{ "title": "x", "dueAt": "0001-01-01T00:00:00Z" }""", "invalid-deadline")]
    [InlineData("""{ "title": "x", "dueAt": "2100-01-01T00:00:00Z" }""", "invalid-deadline")]
    [InlineData("""{ "title": "x" }""", "invalid-request")]
    [InlineData("""{ "title": "x", "dueAt": null }""", "invalid-request")]
    [InlineData("""{ "title": "x", "dueAt": "tomorrow" }""", "invalid-request")]
    [InlineData("""{ "dueAt": "2026-11-30T21:59:00Z" }""", "invalid-request")]
    public async Task A_deadline_out_of_bounds_is_400(string template, string type)
    {
        using var app = new AccountsApp(factory);
        var token = (await app.SignInByEmail(AccountsApp.NewEmail())).AccessToken;
        var json = template.Replace("{201}", new string('a', 201)).Replace("{2001}", new string('n', 2001));

        var request = new HttpRequestMessage(HttpMethod.Post, "/me/deadlines") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await app.Client.SendAsync(request);

        Assert.Equal((HttpStatusCode.BadRequest, type), await AccountsApp.Problem(response));
        Assert.Empty(await List(app, token));
    }

    /// <summary>A PATCH may leave a field out, but can't set <c>title</c>, <c>dueAt</c> or <c>done</c> to null, nor move
    /// the due time out of bounds.</summary>
    [PostgresTheory]
    [InlineData("""{ "title": null }""", "invalid-deadline")]
    [InlineData("""{ "dueAt": null }""", "invalid-request")]
    [InlineData("""{ "done": null }""", "invalid-request")]
    [InlineData("""{ "dueAt": "0001-01-01T00:00:00Z" }""", "invalid-deadline")]
    public async Task An_edit_cant_null_a_required_field(string json, string type)
    {
        using var app = new AccountsApp(factory);
        var token = (await app.SignInByEmail(AccountsApp.NewEmail())).AccessToken;
        var deadline = await Read(await app.Post("/me/deadlines", new { title = "Lab", dueAt = Monday }, token));

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/me/deadlines/{deadline.Id}")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await app.Client.SendAsync(request);

        Assert.Equal((HttpStatusCode.BadRequest, type), await AccountsApp.Problem(response));
        Assert.Equal(deadline, Assert.Single(await List(app, token)));
    }

    /// <summary>A deadline stored before due times were bounded stays editable: only a due time the body sends is
    /// checked.</summary>
    [PostgresFact]
    public async Task A_deadline_stored_out_of_bounds_can_still_be_ticked_off()
    {
        using var app = new AccountsApp(factory);
        var account = await app.SignInByEmail(AccountsApp.NewEmail());
        var deadline = await Read(await app.Post("/me/deadlines", new { title = "Lab", dueAt = Monday }, account.AccessToken));
        await using (var db = factory.CreateDbContext())
        {
            var far = new DateTimeOffset(2150, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await db.Deadlines.Where(d => d.Id == deadline.Id).ExecuteUpdateAsync(set => set.SetProperty(d => d.DueAt, far));
        }

        var done = await Read(await app.Send(HttpMethod.Patch, $"/me/deadlines/{deadline.Id}", new { done = true }, account.AccessToken));

        Assert.Equal((true, 2150), (done.Done, done.DueAt.Year));
    }

    [PostgresFact]
    public async Task An_edit_with_only_some_fields_keeps_the_others()
    {
        using var app = new AccountsApp(factory);
        var token = (await app.SignInByEmail(AccountsApp.NewEmail())).AccessToken;
        var deadline = await Read(await app.Post("/me/deadlines", new { title = "Lab", course = "MSA", dueAt = Monday, notes = "PDF" }, token));

        var moved = await Read(await app.Send(HttpMethod.Patch, $"/me/deadlines/{deadline.Id}", new { dueAt = Monday.AddDays(1) }, token));
        var kept = await Read(await app.Send(HttpMethod.Patch, $"/me/deadlines/{deadline.Id}", new { course = (string?)null }, token));

        Assert.Equal(("Lab", "MSA", "PDF", false, Monday.AddDays(1)), (moved.Title, moved.Course, moved.Notes, moved.Done, moved.DueAt));
        Assert.Equal(("MSA", false), (kept.Course, kept.Done));
    }

    [PostgresFact]
    public async Task An_edit_out_of_bounds_is_400_and_changes_nothing()
    {
        using var app = new AccountsApp(factory);
        var token = (await app.SignInByEmail(AccountsApp.NewEmail())).AccessToken;
        var deadline = await Read(await app.Post("/me/deadlines", new { title = "Lab", dueAt = Monday }, token));

        var response = await app.Send(HttpMethod.Patch, $"/me/deadlines/{deadline.Id}", new { title = "", done = true }, token);

        Assert.Equal((HttpStatusCode.BadRequest, "invalid-deadline"), await AccountsApp.Problem(response));
        Assert.Equal(deadline, Assert.Single(await List(app, token)));
    }

    [PostgresFact]
    public async Task A_student_keeps_at_most_500_deadlines()
    {
        using var app = new AccountsApp(factory);
        var account = await app.SignInByEmail(AccountsApp.NewEmail());
        await using (var db = factory.CreateDbContext())
        {
            db.Deadlines.AddRange(Enumerable.Range(0, Deadline.MaxPerUser).Select(i => new Deadline(account.User.Id, $"d{i}", null, Monday, null, DateTimeOffset.UtcNow)));
            await db.SaveChangesAsync();
        }

        var response = await app.Post("/me/deadlines", new { title = "one more", dueAt = Monday }, account.AccessToken);

        Assert.Equal((HttpStatusCode.Conflict, "deadline-limit"), await AccountsApp.Problem(response));
    }

    /// <summary>Another student's deadline is as unknown as one that doesn't exist.</summary>
    [PostgresFact]
    public async Task Another_students_deadline_cant_be_seen_changed_or_deleted()
    {
        using var app = new AccountsApp(factory);
        var ana = (await app.SignInByEmail(AccountsApp.NewEmail())).AccessToken;
        var ion = (await app.SignInByEmail(AccountsApp.NewEmail())).AccessToken;
        var deadline = await Read(await app.Post("/me/deadlines", new { title = "Ana's", dueAt = Monday }, ana));

        var edit = await app.Send(HttpMethod.Patch, $"/me/deadlines/{deadline.Id}", new { done = true }, ion);
        var delete = await app.Send(HttpMethod.Delete, $"/me/deadlines/{deadline.Id}", bearer: ion);

        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(await List(app, ion));
        Assert.Equal(deadline, Assert.Single(await List(app, ana)));
    }

    [PostgresFact]
    public async Task Deadlines_need_sign_in_and_an_account()
    {
        using var app = new AccountsApp(factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Send(HttpMethod.Get, "/me/deadlines")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Send(HttpMethod.Get, "/me/deadlines", bearer: TestAuth.OwnToken(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Post("/me/deadlines", new { title = "x", dueAt = Monday }, TestAuth.OwnToken(Guid.NewGuid()))).StatusCode);
    }

    /// <summary>A student's own data: never stored by a browser, tagged for revalidation.</summary>
    [PostgresFact]
    public async Task The_list_is_no_store_with_an_ETag()
    {
        using var app = new AccountsApp(factory);
        var token = (await app.SignInByEmail(AccountsApp.NewEmail())).AccessToken;

        var response = await app.Send(HttpMethod.Get, "/me/deadlines", bearer: token);

        Assert.True(response.Headers.CacheControl is { Private: true, NoStore: true });
        Assert.NotNull(response.Headers.ETag);
    }
}
