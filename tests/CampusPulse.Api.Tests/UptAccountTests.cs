using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CampusPulse.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Tests;

/// <summary>UPT Microsoft as a sign-in method: <c>POST /auth/microsoft</c>, linking and unlinking, and the UPT Entra
/// bearer that installed app versions still send.</summary>
public sealed class UptAccountTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    private static string NewOid() => "oid-" + Guid.NewGuid().ToString("N");

    [PostgresFact]
    public async Task Signing_in_with_UPT_makes_an_account_once_with_the_UPT_email()
    {
        using var app = new AccountsApp(factory);
        var oid = NewOid();

        var first = await app.SignInWithUpt(oid);
        var again = await app.SignInWithUpt(oid);

        Assert.True(first.IsNew);
        Assert.Equal(($"{oid}@student.upt.ro", "Ana Pop", true), (first.User.Email, first.User.DisplayName, first.User.UptLinked));
        var identity = Assert.Single(first.User.Identities);
        Assert.Equal(("upt-microsoft", $"{oid}@student.upt.ro"), (identity.Provider, identity.Email));
        Assert.False(again.IsNew);
        Assert.Equal(first.User.Id, again.User.Id);
        Assert.Equal(HttpStatusCode.OK, (await app.Send(HttpMethod.Get, "/me", bearer: first.AccessToken)).StatusCode);
    }

    public static TheoryData<string, string> RefusedTokens => new()
    {
        { "another tenant", TestAuth.Token(tenant: "99999999-2222-3333-4444-555555555555") },
        { "expired", TestAuth.Token(expired: true) },
        { "no access_as_user scope", TestAuth.Token(scope: "User.Read") },
        { "a guest", TestAuth.Token(idp: "https://sts.windows.net/99999999-2222-3333-4444-555555555555/") },
        { "not a token", "nonsense" },
    };

    /// <summary>The token is checked exactly as the Entra bearer checks one.</summary>
    [PostgresTheory]
    [MemberData(nameof(RefusedTokens))]
    public async Task A_refused_Microsoft_token_is_401(string what, string token)
    {
        using var app = new AccountsApp(factory);

        var response = await app.Post("/auth/microsoft", new { accessToken = token });

        Assert.True((HttpStatusCode.Unauthorized, "microsoft-token-invalid") == await AccountsApp.Problem(response), what);
    }

    [PostgresFact]
    public async Task Without_UPT_configured_it_is_503()
    {
        using var app = new AccountsApp(factory, new Dictionary<string, string> { ["Auth:TenantId"] = "", ["Auth:ClientId"] = "" });

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "microsoft-unavailable"),
            await AccountsApp.Problem(await app.Post("/auth/microsoft", new { accessToken = TestAuth.Token(oid: NewOid()) })));
    }

    /// <summary>Both addresses are verified (a code, Microsoft), so an email account and a UPT account with the same
    /// address are one student: signing in with UPT links them.</summary>
    [PostgresFact]
    public async Task UPT_sign_in_links_automatically_to_the_account_with_the_same_verified_email()
    {
        using var app = new AccountsApp(factory);
        var oid = NewOid();
        var byEmail = await app.SignInByEmail($"{oid}@student.upt.ro");

        var byUpt = await app.SignInWithUpt(oid);

        Assert.False(byUpt.IsNew);
        Assert.Equal(byEmail.User.Id, byUpt.User.Id);
        Assert.True(byUpt.User.UptLinked);
        Assert.Equal("Ana Pop", byUpt.User.DisplayName);
    }

    /// <summary>An account made with UPT signs in by its UPT email too.</summary>
    [PostgresFact]
    public async Task An_email_code_to_the_UPT_address_signs_in_to_the_UPT_account()
    {
        using var app = new AccountsApp(factory);
        var oid = NewOid();
        var byUpt = await app.SignInWithUpt(oid);

        var byEmail = await app.SignInByEmail($"{oid}@student.upt.ro");

        Assert.Equal((byUpt.User.Id, false), (byEmail.User.Id, byEmail.IsNew));
    }

    [PostgresFact]
    public async Task A_UPT_account_whose_address_another_account_has_gets_none()
    {
        using var app = new AccountsApp(factory);
        var email = AccountsApp.NewUptEmail();
        var owner = await app.SignInByEmail(email);
        await app.Post("/me/identities/upt-microsoft", new { accessToken = TestAuth.Token(oid: NewOid(), email: email) }, owner.AccessToken);

        var other = await app.SignInWithUpt(NewOid(), email);

        Assert.True(other.IsNew);
        Assert.NotEqual(owner.User.Id, other.User.Id);
        Assert.Null(other.User.Email);
    }

    /// <summary>Only a UPT address is trusted from a token (Microsoft doesn't vouch for the email claim): another
    /// address links to nothing, doesn't become the account's email, and a code to it doesn't open the UPT
    /// account.</summary>
    [PostgresFact]
    public async Task A_token_address_outside_UPT_is_not_trusted()
    {
        using var app = new AccountsApp(factory);
        var taken = AccountsApp.NewEmail();
        var owner = await app.SignInByEmail(taken);
        var lone = AccountsApp.NewEmail();

        var upt = await app.SignInWithUpt(NewOid(), taken);
        var uptOnly = await app.SignInWithUpt(NewOid(), lone);
        var byCode = await app.SignInByEmail(lone);

        Assert.Equal((true, (string?)null), (upt.IsNew, upt.User.Email));
        Assert.NotEqual(owner.User.Id, upt.User.Id);
        Assert.Equal(taken, Assert.Single(upt.User.Identities).Email);
        Assert.Null(uptOnly.User.Email);
        Assert.True(byCode.IsNew);
        Assert.NotEqual(uptOnly.User.Id, byCode.User.Id);
        Assert.Equal(owner.User.Id, (await app.SignInByEmail(taken)).User.Id);
    }

    /// <summary>Installed app versions: a UPT Entra token as the bearer, and POST /me to make the account, reach the same
    /// account as our tokens, for /me and the timetable alike.</summary>
    [PostgresFact]
    public async Task The_UPT_bearer_of_installed_apps_reaches_the_same_account()
    {
        using var app = new AccountsApp(factory);
        var oid = NewOid();
        var entra = TestAuth.Token(oid: oid);
        var created = await app.Post("/me", new { }, entra);
        var ours = await app.SignInWithUpt(oid);

        var meByEntra = await (await app.Send(HttpMethod.Get, "/me", bearer: entra)).Content.ReadFromJsonAsync<JsonElement>();
        var timetableByEntra = await app.Send(HttpMethod.Get, "/me/timetable", bearer: entra);
        var timetableByOurs = await app.Send(HttpMethod.Get, "/me/timetable", bearer: ours.AccessToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.False(ours.IsNew);
        Assert.Equal(ours.User.Id, meByEntra.GetProperty("id").GetGuid());
        Assert.True(meByEntra.GetProperty("uptLinked").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, timetableByEntra.StatusCode);
        Assert.Equal(await timetableByEntra.Content.ReadAsStringAsync(), await timetableByOurs.Content.ReadAsStringAsync());
    }

    /// <summary>A student the previous release made (after a rollback: a <c>users.entra_object_id</c> but no identity) is
    /// still found, and gets its identity on the next sign-in.</summary>
    [PostgresFact]
    public async Task An_account_the_previous_release_made_is_found_and_gets_its_identity()
    {
        using var app = new AccountsApp(factory);
        var oid = NewOid();
        Guid id;
        await using (var db = factory.CreateDbContext())
        {
            var legacy = User.FromUpt(oid, $"{oid}@student.upt.ro", "Ana", DateTimeOffset.UtcNow);
            db.Users.Add(legacy);
            await db.SaveChangesAsync();
            id = legacy.Id;
        }

        var me = await app.Send(HttpMethod.Get, "/me", bearer: TestAuth.Token(oid: oid));
        var signedIn = await app.SignInWithUpt(oid);

        Assert.Equal(id, (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal((id, false), (signedIn.User.Id, signedIn.IsNew));
        await using var after = factory.CreateDbContext();
        Assert.True(await after.UserIdentities.AnyAsync(i => i.UserId == id && i.Subject == oid));
    }

    [PostgresFact]
    public async Task An_email_account_links_UPT_and_then_the_UPT_bearer_finds_it()
    {
        using var app = new AccountsApp(factory);
        var account = await app.SignInByEmail(AccountsApp.NewEmail());
        var oid = NewOid();

        var linked = await app.Post("/me/identities/upt-microsoft", new { accessToken = TestAuth.Token(oid: oid) }, account.AccessToken);
        var again = await app.Post("/me/identities/upt-microsoft", new { accessToken = TestAuth.Token(oid: oid) }, account.AccessToken);
        var byEntra = await app.Send(HttpMethod.Get, "/me", bearer: TestAuth.Token(oid: oid));

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var body = await linked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("uptLinked").GetBoolean());
        Assert.Equal("Ana Pop", body.GetProperty("displayName").GetString());
        Assert.Equal(account.User.Email, body.GetProperty("email").GetString());   // the account's own address stays
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(account.User.Id, (await byEntra.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    [PostgresFact]
    public async Task A_UPT_account_of_another_account_is_identity_in_use()
    {
        using var app = new AccountsApp(factory);
        var oid = NewOid();
        await app.SignInWithUpt(oid);
        var other = await app.SignInByEmail(AccountsApp.NewEmail());

        Assert.Equal((HttpStatusCode.Conflict, "identity-in-use"), await AccountsApp.Problem(
            await app.Post("/me/identities/upt-microsoft", new { accessToken = TestAuth.Token(oid: oid) }, other.AccessToken)));
    }

    [PostgresFact]
    public async Task A_second_UPT_account_is_already_linked()
    {
        using var app = new AccountsApp(factory);
        var account = await app.SignInWithUpt(NewOid());

        Assert.Equal((HttpStatusCode.Conflict, "already-linked"), await AccountsApp.Problem(
            await app.Post("/me/identities/upt-microsoft", new { accessToken = TestAuth.Token(oid: NewOid()) }, account.AccessToken)));
    }

    [PostgresFact]
    public async Task Linking_needs_a_valid_UPT_token()
    {
        using var app = new AccountsApp(factory);
        var account = await app.SignInByEmail(AccountsApp.NewEmail());

        Assert.Equal((HttpStatusCode.Unauthorized, "microsoft-token-invalid"), await AccountsApp.Problem(
            await app.Post("/me/identities/upt-microsoft", new { accessToken = TestAuth.Token(expired: true) }, account.AccessToken)));
    }

    /// <summary>Linking UPT gives an account without a verified email the UPT one.</summary>
    [PostgresFact]
    public async Task Linking_gives_an_account_without_an_email_the_UPT_one()
    {
        using var app = new AccountsApp(factory);
        var noEmail = await app.SignInWithUpt(NewOid(), email: null);
        await using (var db = factory.CreateDbContext())
        {
            await db.UserIdentities.Where(i => i.UserId == noEmail.User.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == noEmail.User.Id).ExecuteUpdateAsync(set => set.SetProperty(u => u.EntraObjectId, (string?)null));
        }

        var oid = NewOid();
        var linked = await app.Post("/me/identities/upt-microsoft", new { accessToken = TestAuth.Token(oid: oid) }, noEmail.AccessToken);

        Assert.Null(noEmail.User.Email);
        Assert.Equal($"{oid}@student.upt.ro", (await linked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("email").GetString());
    }

    [PostgresFact]
    public async Task Unlinking_UPT_leaves_the_email_sign_in_and_the_UPT_bearer_finds_nothing()
    {
        using var app = new AccountsApp(factory);
        var account = await app.SignInByEmail(AccountsApp.NewEmail());
        var oid = NewOid();
        await app.Post("/me/identities/upt-microsoft", new { accessToken = TestAuth.Token(oid: oid) }, account.AccessToken);

        var unlinked = await app.Send(HttpMethod.Delete, "/me/identities/upt-microsoft", bearer: account.AccessToken);
        var again = await app.Send(HttpMethod.Delete, "/me/identities/upt-microsoft", bearer: account.AccessToken);
        var me = await (await app.Send(HttpMethod.Get, "/me", bearer: account.AccessToken)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (unlinked.StatusCode, again.StatusCode));
        Assert.False(me.GetProperty("uptLinked").GetBoolean());
        Assert.Equal(0, me.GetProperty("identities").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Send(HttpMethod.Get, "/me", bearer: TestAuth.Token(oid: oid))).StatusCode);
    }

    /// <summary>An account with no verified email could only sign in with UPT: unlinking it is refused.</summary>
    [PostgresFact]
    public async Task Unlinking_the_last_sign_in_method_is_refused()
    {
        using var app = new AccountsApp(factory);
        var account = await app.SignInWithUpt(NewOid(), email: null);

        var response = await app.Send(HttpMethod.Delete, "/me/identities/upt-microsoft", bearer: account.AccessToken);

        Assert.Null(account.User.Email);
        Assert.Equal((HttpStatusCode.Conflict, "last-sign-in-method"), await AccountsApp.Problem(response));
        Assert.True((await (await app.Send(HttpMethod.Get, "/me", bearer: account.AccessToken)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("uptLinked").GetBoolean());
    }

    [PostgresFact]
    public async Task Concurrent_first_UPT_sign_ins_make_one_account()
    {
        using var app = new AccountsApp(factory);
        var oid = NewOid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => app.SignInWithUpt(oid)));

        Assert.Single(results, r => r.IsNew);
        Assert.Single(results.Select(r => r.User.Id).Distinct());
    }
}
