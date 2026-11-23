using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CampusPulse.Api.Accounts;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Tests;

/// <summary>Signing in with an email and a one-time code (<c>/auth/email/*</c>).</summary>
public sealed class EmailSignInTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    [PostgresFact]
    public async Task Start_sends_a_code_and_answers_202_with_its_lifetime()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewEmail();

        var response = await app.StartEmail("  " + email.ToUpperInvariant() + " ");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((600, 60), (body.GetProperty("expiresInSeconds").GetInt32(), body.GetProperty("resendAfterSeconds").GetInt32()));
        var message = Assert.Single(app.Email.Sent);
        Assert.Equal(email, message.To);
        var code = CapturingEmailSender.Code(message);
        Assert.Equal($"Your CampusPulse code: {code}", message.Subject);
        Assert.Contains("expires in 10 minutes", message.Text);
        Assert.Contains("If you didn't ask for this, ignore this email.", message.Text);
        Assert.Contains(code, message.Html);
        Assert.DoesNotContain("http", message.Text + message.Html.Replace("<!DOCTYPE html>", ""), StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task The_email_is_in_Romanian_when_asked()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewEmail();

        await app.StartEmail(email, "ro");

        var message = Assert.Single(app.Email.Sent);
        Assert.StartsWith("Codul tău CampusPulse: ", message.Subject);
        Assert.Contains("Codul expiră în 10 minute.", message.Text);
    }

    /// <summary>The answer is the same for an address with an account and one without: start says nothing about who is
    /// registered.</summary>
    [PostgresFact]
    public async Task Start_answers_the_same_whether_or_not_an_account_has_the_address()
    {
        using var app = new AccountsApp(factory);
        var known = AccountsApp.NewEmail();
        await app.SignInByEmail(known);
        var unknown = AccountsApp.NewEmail();

        var forKnown = await app.StartEmail(known);
        var forUnknown = await app.StartEmail(unknown);

        Assert.Equal(forKnown.StatusCode, forUnknown.StatusCode);
        Assert.Equal(await forKnown.Content.ReadAsStringAsync(), await forUnknown.Content.ReadAsStringAsync());
        Assert.Contains(app.Email.Sent, m => m.To == unknown);
    }

    [PostgresTheory]
    [InlineData("""{ "email": "not-an-email", "language": "en" }""", "invalid-email")]
    [InlineData("""{ "email": "Ana <ana@example.com>", "language": "en" }""", "invalid-email")]
    [InlineData("""{ "email": "ana@example.com" }""", null)]
    [InlineData("""{ "email": "ana@example.com", "language": "de" }""", null)]
    public async Task Start_refuses_what_isnt_an_address_or_a_language(string json, string? type)
    {
        using var app = new AccountsApp(factory);

        var response = await app.Client.PostAsync("/auth/email/start", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        if (type is not null)
        {
            Assert.Equal((HttpStatusCode.BadRequest, type), await AccountsApp.Problem(response));
        }

        Assert.Empty(app.Email.Sent);
    }

    [PostgresFact]
    public async Task Without_a_sender_start_is_503_email_unavailable()
    {
        using var app = new AccountsApp(factory);
        app.Email.IsConfigured = false;

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "email-unavailable"), await AccountsApp.Problem(await app.StartEmail(AccountsApp.NewEmail())));
    }

    /// <summary>A code that couldn't be sent is taken back: it doesn't work and doesn't count against the address.</summary>
    [PostgresFact]
    public async Task A_failed_send_is_503_and_takes_the_code_back()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewEmail();
        app.Email.Refuse = true;

        var failed = await app.StartEmail(email);
        app.Email.Refuse = false;
        var after = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => app.StartEmail(email)));

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "email-unavailable"), await AccountsApp.Problem(failed));
        Assert.All(after, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
    }

    [PostgresFact]
    public async Task The_right_code_signs_in_once_and_a_new_account_is_made_the_first_time()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewEmail();
        await app.StartEmail(email);
        var code = app.Email.CodeFor(email);

        var first = await AccountsApp.Tokens(await app.VerifyEmail(email.ToUpperInvariant(), code));
        var reused = await app.VerifyEmail(email, code);
        var second = await app.SignInByEmail(email);

        Assert.True(first.IsNew);
        Assert.Equal((email, email.Split('@')[0], false), (first.User.Email, first.User.DisplayName, first.User.UptLinked));
        Assert.Empty(first.User.Identities);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid-code"), await AccountsApp.Problem(reused));
        Assert.False(second.IsNew);
        Assert.Equal(first.User.Id, second.User.Id);
        Assert.InRange(first.AccessTokenExpiresAt - app.Clock.GetUtcNow(), TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        Assert.InRange(first.RefreshTokenExpiresAt - app.Clock.GetUtcNow(), TimeSpan.FromDays(59.9), TimeSpan.FromDays(60));

        var me = await app.Send(HttpMethod.Get, "/me", bearer: first.AccessToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(first.User.Id, (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    [PostgresFact]
    public async Task A_new_code_replaces_the_last_one()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewEmail();
        await app.StartEmail(email);
        var old = app.Email.CodeFor(email);
        await app.StartEmail(email);
        var latest = app.Email.CodeFor(email);

        if (old != latest)
        {
            Assert.Equal((HttpStatusCode.BadRequest, "invalid-code"), await AccountsApp.Problem(await app.VerifyEmail(email, old)));
        }

        Assert.Equal(HttpStatusCode.OK, (await app.VerifyEmail(email, latest)).StatusCode);
    }

    [PostgresFact]
    public async Task Five_wrong_codes_burn_it_and_then_even_the_right_one_is_refused()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewEmail();
        await app.StartEmail(email);
        var code = app.Email.CodeFor(email);
        var wrong = code == "000000" ? "111111" : "000000";

        var attempts = new List<(HttpStatusCode, string?)>();
        for (var i = 0; i < 5; i++)
        {
            attempts.Add(await AccountsApp.Problem(await app.VerifyEmail(email, wrong)));
        }

        var right = await app.VerifyEmail(email, code);

        Assert.Equal(Enumerable.Repeat((HttpStatusCode.BadRequest, (string?)"invalid-code"), 4).Append((HttpStatusCode.BadRequest, "code-burned")), attempts);
        Assert.Equal((HttpStatusCode.BadRequest, "code-burned"), await AccountsApp.Problem(right));
        await using var db = factory.CreateDbContext();
        Assert.Equal(5, (await db.EmailSignInCodes.SingleAsync(c => c.Email == email)).Attempts);
    }

    [PostgresFact]
    public async Task A_code_expires_after_ten_minutes()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewEmail();
        await app.StartEmail(email);
        app.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal((HttpStatusCode.BadRequest, "invalid-code"), await AccountsApp.Problem(await app.VerifyEmail(email, app.Email.CodeFor(email))));
    }

    [PostgresFact]
    public async Task A_code_for_an_address_that_asked_for_none_is_invalid()
    {
        using var app = new AccountsApp(factory);

        Assert.Equal((HttpStatusCode.BadRequest, "invalid-code"), await AccountsApp.Problem(await app.VerifyEmail(AccountsApp.NewEmail(), "123456")));
    }

    /// <summary>Only a salted hash is stored: the code itself is nowhere in the row.</summary>
    [PostgresFact]
    public async Task The_code_is_stored_only_as_a_salted_hash()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewEmail();
        await app.StartEmail(email);
        var code = app.Email.CodeFor(email);

        await using var db = factory.CreateDbContext();
        var row = await db.EmailSignInCodes.SingleAsync(c => c.Email == email);
        Assert.Equal((32, 16), (row.CodeHash!.Length, row.Salt!.Length));
        Assert.DoesNotContain(code, Convert.ToHexString(row.CodeHash) + Convert.ToHexString(row.Salt) + Convert.ToBase64String(row.CodeHash),
            StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task An_address_gets_three_codes_per_15_minutes_and_ten_a_day()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewEmail();

        var first = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => app.StartEmail(email)));
        var fourth = await app.StartEmail(email);
        var other = await app.StartEmail(AccountsApp.NewEmail());

        Assert.All(first, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal((HttpStatusCode.TooManyRequests, "too-many-codes"), await AccountsApp.Problem(fourth));
        Assert.InRange(int.Parse(fourth.Headers.GetValues("Retry-After").Single()), 1, 900);
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);

        var sent = 3;
        for (var window = 1; sent < 10; window++)
        {
            app.Clock.Advance(TimeSpan.FromMinutes(15));
            for (var i = 0; i < 3 && sent < 10; i++, sent++)
            {
                Assert.Equal(HttpStatusCode.Accepted, (await app.StartEmail(email)).StatusCode);
            }
        }

        app.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal((HttpStatusCode.TooManyRequests, "too-many-codes"), await AccountsApp.Problem(await app.StartEmail(email)));
        app.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(HttpStatusCode.Accepted, (await app.StartEmail(email)).StatusCode);
    }

    /// <summary>Per client IP: each start sends an email, so they're limited per hour (here 2; 20 by default).</summary>
    [PostgresFact]
    public async Task Starts_are_limited_per_client_address()
    {
        using var app = new AccountsApp(factory, new Dictionary<string, string> { ["RateLimits:EmailStart:Limit"] = "2" });

        var responses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            responses.Add((await app.StartEmail(AccountsApp.NewEmail())).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests], responses);
    }

    /// <summary>Every /auth call takes from a per-IP bucket of its own (here 3; 60 a minute by default), not from the
    /// write bucket.</summary>
    [PostgresFact]
    public async Task Auth_calls_have_their_own_per_address_limit()
    {
        using var app = new AccountsApp(factory, new Dictionary<string, string>
        {
            ["RateLimits:Auth:Burst"] = "3",
            ["RateLimits:Auth:PerMinute"] = "1",
            ["RateLimits:Writes:Burst"] = "1",
            ["RateLimits:Writes:PerMinute"] = "1",
        });

        var responses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            responses.Add((await app.Post("/auth/sign-out", new { refreshToken = "" })).StatusCode);
        }

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.TooManyRequests], responses);
    }

    /// <summary>No code, token or address ever reaches a log line.</summary>
    [PostgresFact]
    public async Task Sign_in_logs_no_code_token_or_address()
    {
        var logs = new CollectingLoggerProvider();
        using var app = new AccountsApp(factory, logs: logs);
        var email = AccountsApp.NewEmail();
        await app.StartEmail(email);
        var code = app.Email.CodeFor(email);
        await app.VerifyEmail(email, code == "000000" ? "111111" : "000000");
        var tokens = await AccountsApp.Tokens(await app.VerifyEmail(email, code));
        await app.Post("/auth/refresh", new { refreshToken = tokens.RefreshToken });

        var secrets = new[] { email, email.Split('@')[0], code, tokens.AccessToken, tokens.RefreshToken };
        Assert.DoesNotContain(logs.Entries, e => secrets.Any(s =>
            e.Message.Contains(s, StringComparison.OrdinalIgnoreCase)
            || e.ScopeValues.Any(kv => kv.Value?.ToString()?.Contains(s, StringComparison.OrdinalIgnoreCase) == true)));
        Assert.Contains(logs.Entries, e => e.Category == AuthEndpoints.LogCategory && e.Message == "Email sign-in code sent");
        Assert.Contains(logs.Entries, e => e.Category == AuthEndpoints.LogCategory && e.Message == "Signed in with an email code (new account: True)");
    }
}
