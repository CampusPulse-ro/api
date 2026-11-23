using System.Net;
using CampusPulse.Domain.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CampusPulse.Api.Tests;

/// <summary>Our tokens: the access token as a bearer, and the refresh token's rotation, reuse detection and sign-out.</summary>
public sealed class SessionTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    [PostgresFact]
    public async Task The_access_token_is_ours_and_says_how_the_student_signed_in()
    {
        using var app = new AccountsApp(factory);

        var tokens = await app.SignInByEmail(AccountsApp.NewEmail());

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(tokens.AccessToken);
        Assert.Equal(("campuspulse", "HS256", tokens.User.Id.ToString()), (jwt.Issuer, jwt.Alg, jwt.Subject));
        Assert.Equal(["campuspulse-api"], jwt.Audiences);
        Assert.Equal(["email"], jwt.GetPayloadValue<string[]>("amr"));
    }

    [PostgresFact]
    public async Task A_refresh_rotates_both_tokens_and_the_old_refresh_token_stops_working()
    {
        using var app = new AccountsApp(factory);
        var first = await app.SignInByEmail(AccountsApp.NewEmail());

        var second = await AccountsApp.Tokens(await app.Post("/auth/refresh", new { refreshToken = first.RefreshToken }));

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal((first.User.Id, false), (second.User.Id, second.IsNew));
        Assert.Equal(HttpStatusCode.OK, (await app.Send(HttpMethod.Get, "/me", bearer: second.AccessToken)).StatusCode);
        Assert.Equal(["email"], new JsonWebTokenHandler().ReadJsonWebToken(second.AccessToken).GetPayloadValue<string[]>("amr"));
        await using var db = factory.CreateDbContext();
        var rows = await db.RefreshTokens.Where(t => t.UserId == first.User.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Select(r => r.FamilyId).Distinct());
        Assert.DoesNotContain(rows, r => r.TokenHash == first.RefreshToken || r.TokenHash == second.RefreshToken);
    }

    /// <summary>Showing a token that was already replaced (more than a minute ago) means it was copied: the whole
    /// session is revoked, the newest token too, and the other sessions of the account are not.</summary>
    [PostgresFact]
    public async Task Reusing_a_replaced_refresh_token_revokes_its_whole_session()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewEmail();
        var phone = await app.SignInByEmail(email);
        var laptop = await app.SignInByEmail(email);
        var rotated = await AccountsApp.Tokens(await app.Post("/auth/refresh", new { refreshToken = phone.RefreshToken }));
        app.Clock.Advance(RefreshToken.RetryWindow + TimeSpan.FromSeconds(1));

        var reuse = await app.Post("/auth/refresh", new { refreshToken = phone.RefreshToken });
        var newest = await app.Post("/auth/refresh", new { refreshToken = rotated.RefreshToken });
        var other = await app.Post("/auth/refresh", new { refreshToken = laptop.RefreshToken });

        Assert.Equal((HttpStatusCode.Unauthorized, "refresh-invalid"), await AccountsApp.Problem(reuse));
        Assert.Equal((HttpStatusCode.Unauthorized, "refresh-invalid"), await AccountsApp.Problem(newest));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    /// <summary>A refresh whose answer got lost can be sent again for a minute, as long as the token that answer held
    /// is unused: it gets that same token again, so the family keeps one live token. Once that token is used, the old
    /// one is a copy again.</summary>
    [PostgresFact]
    public async Task A_lost_refresh_answer_can_be_retried_for_a_minute_and_gets_the_same_token()
    {
        using var app = new AccountsApp(factory);
        var first = await app.SignInByEmail(AccountsApp.NewEmail());
        var lost = await AccountsApp.Tokens(await app.Post("/auth/refresh", new { refreshToken = first.RefreshToken }));
        app.Clock.Advance(TimeSpan.FromSeconds(30));

        var retried = await AccountsApp.Tokens(await app.Post("/auth/refresh", new { refreshToken = first.RefreshToken }));

        Assert.Equal(lost.RefreshToken, retried.RefreshToken);
        await using (var db = factory.CreateDbContext())
        {
            var live = Assert.Single(await db.RefreshTokens.Where(t => t.UserId == first.User.Id && t.RevokedAt == null).ToListAsync());
            Assert.Equal(RefreshToken.HashOf(retried.RefreshToken), live.TokenHash);
        }

        var next = await AccountsApp.Tokens(await app.Post("/auth/refresh", new { refreshToken = retried.RefreshToken }));
        Assert.Equal(first.User.Id, next.User.Id);
        Assert.Equal((HttpStatusCode.Unauthorized, "refresh-invalid"),
            await AccountsApp.Problem(await app.Post("/auth/refresh", new { refreshToken = first.RefreshToken })));
        Assert.Equal((HttpStatusCode.Unauthorized, "refresh-invalid"),
            await AccountsApp.Problem(await app.Post("/auth/refresh", new { refreshToken = next.RefreshToken })));
    }

    /// <summary>After the minute, showing the original token again is a reuse that ends the session, its live token
    /// (the one the retries got) included.</summary>
    [PostgresFact]
    public async Task After_the_minute_a_retried_token_is_a_reuse_that_ends_the_session()
    {
        using var app = new AccountsApp(factory);
        var first = await app.SignInByEmail(AccountsApp.NewEmail());
        await app.Post("/auth/refresh", new { refreshToken = first.RefreshToken });
        app.Clock.Advance(TimeSpan.FromSeconds(30));
        var retried = await AccountsApp.Tokens(await app.Post("/auth/refresh", new { refreshToken = first.RefreshToken }));
        app.Clock.Advance(RefreshToken.RetryWindow);

        var reuse = await app.Post("/auth/refresh", new { refreshToken = first.RefreshToken });
        var live = await app.Post("/auth/refresh", new { refreshToken = retried.RefreshToken });

        Assert.Equal((HttpStatusCode.Unauthorized, "refresh-invalid"), await AccountsApp.Problem(reuse));
        Assert.Equal((HttpStatusCode.Unauthorized, "refresh-invalid"), await AccountsApp.Problem(live));
    }

    /// <summary>Two refreshes of one token at once (a client that timed out and retried while the first was still
    /// running) both answer with the same refresh token, whichever finishes first: the client keeps a live token either
    /// way.</summary>
    [PostgresFact]
    public async Task Two_refreshes_of_one_token_at_once_get_the_same_refresh_token()
    {
        using var app = new AccountsApp(factory);
        var first = await app.SignInByEmail(AccountsApp.NewEmail());

        var answers = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => app.Post("/auth/refresh", new { refreshToken = first.RefreshToken })));

        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.OK, answer.StatusCode));
        var tokens = await Task.WhenAll(answers.Select(AccountsApp.Tokens));
        Assert.Equal(tokens[0].RefreshToken, tokens[1].RefreshToken);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Single(await db.RefreshTokens.Where(t => t.UserId == first.User.Id && t.RevokedAt == null).ToListAsync());
        }

        app.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.OK, (await app.Post("/auth/refresh", new { refreshToken = tokens[0].RefreshToken })).StatusCode);
    }

    [PostgresFact]
    public async Task A_refresh_token_expires_after_60_days()
    {
        using var app = new AccountsApp(factory);
        var tokens = await app.SignInByEmail(AccountsApp.NewEmail());
        app.Clock.Advance(TimeSpan.FromDays(60));

        Assert.Equal((HttpStatusCode.Unauthorized, "refresh-invalid"),
            await AccountsApp.Problem(await app.Post("/auth/refresh", new { refreshToken = tokens.RefreshToken })));
    }

    [PostgresTheory]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task An_unknown_refresh_token_is_refused(string token)
    {
        using var app = new AccountsApp(factory);

        Assert.Equal((HttpStatusCode.Unauthorized, "refresh-invalid"),
            await AccountsApp.Problem(await app.Post("/auth/refresh", new { refreshToken = token })));
    }

    [PostgresFact]
    public async Task Signing_out_ends_the_session_and_is_idempotent()
    {
        using var app = new AccountsApp(factory);
        var tokens = await app.SignInByEmail(AccountsApp.NewEmail());
        var rotated = await AccountsApp.Tokens(await app.Post("/auth/refresh", new { refreshToken = tokens.RefreshToken }));

        var signOut = await app.Post("/auth/sign-out", new { refreshToken = rotated.RefreshToken });
        var again = await app.Post("/auth/sign-out", new { refreshToken = rotated.RefreshToken });
        var unknown = await app.Post("/auth/sign-out", new { refreshToken = "nothing-like-it" });
        var refresh = await app.Post("/auth/refresh", new { refreshToken = rotated.RefreshToken });

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent),
            (signOut.StatusCode, again.StatusCode, unknown.StatusCode));
        Assert.Equal((HttpStatusCode.Unauthorized, "refresh-invalid"), await AccountsApp.Problem(refresh));
        await using var db = factory.CreateDbContext();
        Assert.All(await db.RefreshTokens.Where(t => t.UserId == tokens.User.Id).ToListAsync(), t => Assert.NotNull(t.RevokedAt));
    }

    public static TheoryData<string, string> BadTokens => new()
    {
        { "expired", TestAuth.OwnToken(Guid.NewGuid(), expired: true) },
        { "another key", TestAuth.OwnToken(Guid.NewGuid(), key: new byte[32]) },
        { "another audience", TestAuth.OwnToken(Guid.NewGuid(), audience: "someone-else") },
        { "no account id", TestAuth.OwnToken(Guid.NewGuid(), subject: "not-a-guid") },
    };

    [PostgresTheory]
    [MemberData(nameof(BadTokens))]
    public async Task Our_bearer_refuses_a_bad_token(string what, string token)
    {
        using var app = new AccountsApp(factory);

        var response = await app.Send(HttpMethod.Get, "/me", bearer: token);

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, what);
    }

    /// <summary>A valid token of an account that is gone (deleted from another device) finds no account.</summary>
    [PostgresFact]
    public async Task A_token_of_a_deleted_account_finds_none()
    {
        using var app = new AccountsApp(factory);
        var tokens = await app.SignInByEmail(AccountsApp.NewEmail());

        Assert.Equal(HttpStatusCode.NoContent, (await app.Send(HttpMethod.Delete, "/me", bearer: tokens.AccessToken)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await app.Send(HttpMethod.Get, "/me", bearer: tokens.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Post("/me", new { }, tokens.AccessToken)).StatusCode);
        Assert.Equal((HttpStatusCode.Unauthorized, "refresh-invalid"),
            await AccountsApp.Problem(await app.Post("/auth/refresh", new { refreshToken = tokens.RefreshToken })));
    }

    [PostgresFact]
    public async Task Without_a_signing_key_no_session_is_issued()
    {
        using var app = new AccountsApp(factory, new Dictionary<string, string> { ["Auth:SigningKey"] = "" });

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "sign-in-unavailable"), await AccountsApp.Problem(await app.StartEmail(AccountsApp.NewEmail())));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "sign-in-unavailable"),
            await AccountsApp.Problem(await app.Post("/auth/refresh", new { refreshToken = "x" })));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Send(HttpMethod.Get, "/me", bearer: TestAuth.OwnToken(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public void A_signing_key_must_be_base64_of_at_least_32_bytes()
    {
        using var tooShort = new KeyFactory(Convert.ToBase64String(new byte[16]));
        using var notBase64 = new KeyFactory("not base64!");

        Assert.Contains("at least 32 bytes", Assert.ThrowsAny<Exception>(() => tooShort.CreateClient()).ToString());
        Assert.Contains("must be base64", Assert.ThrowsAny<Exception>(() => notBase64.CreateClient()).ToString());
    }

    private sealed class KeyFactory(string key) : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres", "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
            builder.UseSetting("Auth:SigningKey", key);
        }
    }
}
