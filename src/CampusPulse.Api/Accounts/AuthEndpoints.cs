using System.Globalization;
using CampusPulse.Api.Accounts.Email;
using CampusPulse.Api.Auth;
using CampusPulse.Api.RateLimiting;
using CampusPulse.Api.Users;
using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Accounts;

/// <summary>Body of <c>POST /auth/email/start</c>: the address, and the language of the email (the app's).</summary>
public sealed record EmailStartRequest(string Email, SignInLanguage Language);

/// <summary>Body of <c>POST /auth/email/start</c>'s 202: how long the code is valid, and when the app may offer to send
/// another.</summary>
public sealed record EmailStartResponse(int ExpiresInSeconds, int ResendAfterSeconds);

/// <summary>Body of <c>POST /auth/email/verify</c>.</summary>
public sealed record EmailVerifyRequest(string Email, string Code);

/// <summary>Body of <c>POST /auth/microsoft</c> and <c>POST /me/identities/upt-microsoft</c>: a UPT Entra access token
/// for this API (<c>api://&lt;client-id&gt;/access_as_user</c>), as the apps get today.</summary>
public sealed record MicrosoftTokenRequest(string AccessToken);

/// <summary>Body of <c>POST /auth/refresh</c> and <c>POST /auth/sign-out</c>.</summary>
public sealed record RefreshTokenRequest(string RefreshToken);

/// <summary>
/// Signing in (CampusPulse-ro/api#63): with an email and a one-time code, or with UPT Microsoft; then refreshing and
/// signing out. No caller token needed. Errors are ProblemDetails whose <c>type</c> says which (<see cref="Problems"/>).
/// Logs counts and outcomes only: never a code, a token or an address.
/// </summary>
public static partial class AuthEndpoints
{
    public const string LogCategory = "CampusPulse.Api.Accounts";

    public static readonly int ResendAfterSeconds = 60;

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").AllowAnonymous().WithTags("Sign-in");

        auth.MapPost("/email/start", StartEmail)
            .WithName("StartEmailSignIn")
            .RequireRateLimiting(RateLimitSetup.EmailStartPolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        auth.MapPost("/email/verify", VerifyEmail)
            .WithName("VerifyEmailSignIn")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        auth.MapPost("/microsoft", SignInWithMicrosoft)
            .WithName("SignInWithMicrosoft")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        auth.MapPost("/refresh", Refresh)
            .WithName("RefreshSession")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        auth.MapPost("/sign-out", SignOut)
            .WithName("SignOut");

        return app;
    }

    /// <summary>
    /// Sends a sign-in code to the address: 202 whether or not an account has it (signing in creates one, so the answer
    /// says nothing about who is registered). A new code replaces the last one. Limits: 3 codes per address per 15
    /// minutes and 10 per day (429 with Retry-After, type <c>too-many-codes</c>), and per client IP (the
    /// <c>email-start</c> rate limit). 503 <c>email-unavailable</c> when no email can be sent.
    /// </summary>
    internal static async Task<Results<Accepted<EmailStartResponse>, ProblemHttpResult>> StartEmail(EmailStartRequest request,
        CampusPulseDbContext db, IEmailSender sender, CampusPulseTokens tokens, TimeProvider time, HttpContext context,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (!Emails.IsValid(request.Email))
        {
            return InvalidEmail();
        }

        if (!tokens.IsConfigured)
        {
            return SignInUnavailable();
        }

        if (!sender.IsConfigured)
        {
            return EmailUnavailable("No email sender is configured on this server.");
        }

        var email = Emails.Normalize(request.Email);
        var now = time.GetUtcNow();
        string code;
        EmailSignInCode.Replaced replaced;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var row = await LockCodeAsync(db, email, create: true, cancellationToken);
            if (row!.SendRefusal(now) is { } wait)
            {
                var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
                context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return Problems.Of(StatusCodes.Status429TooManyRequests, "too-many-codes", "Too many codes for this address",
                    $"Try again in {seconds} s.");
            }

            code = EmailSignInCode.NewCode();
            replaced = row.Issue(code, now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Sent and, when that fails, withdrawn whether or not the client is still waiting: a code issued and counted
        // must not be left behind by a dropped connection. The sender's own timeout bounds it.
        var logger = loggers.CreateLogger(LogCategory);
        var message = SignInEmail.Compose(email, code, request.Language);
        bool sent;
        try
        {
            sent = await sender.SendAsync(message, CancellationToken.None);
        }
        catch (Exception e)
        {
            // Any sender fault is a failed send: the code is withdrawn below rather than left issued behind a 500. The
            // exception's message isn't logged (a sender's may quote the address), its type and stack trace are.
            LogSenderFailed(logger, e.GetType().Name, e.StackTrace);
            sent = false;
        }

        if (!sent)
        {
            await WithdrawAsync(db, email, now, replaced, CancellationToken.None);
            LogCodeNotDelivered(logger);
            return EmailUnavailable("The email couldn't be sent. Try again later.");
        }

        LogCodeSent(logger);
        return TypedResults.Accepted((string?)null,
            new EmailStartResponse((int)EmailSignInCode.Lifetime.TotalSeconds, ResendAfterSeconds));
    }

    /// <summary>
    /// Signs in with the code sent to the address: the account with that verified email, else one whose linked UPT
    /// account has it, else a new one (<c>isNew</c>). 400 <c>invalid-code</c> when it is wrong or expired (the attempt is
    /// counted); after 5 wrong attempts the code is burned: 400 <c>code-burned</c>, ask for a new one.
    /// </summary>
    internal static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> VerifyEmail(EmailVerifyRequest request,
        CampusPulseDbContext db, CampusPulseTokens tokens, TimeProvider time, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (!Emails.IsValid(request.Email))
        {
            return InvalidEmail();
        }

        if (!tokens.IsConfigured)
        {
            return SignInUnavailable();
        }

        var email = Emails.Normalize(request.Email);
        var now = time.GetUtcNow();
        var logger = loggers.CreateLogger(LogCategory);
        CodeCheck check;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var row = await LockCodeAsync(db, email, create: false, cancellationToken);
            check = row?.Check((request.Code ?? "").Trim(), now) ?? CodeCheck.Invalid;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (check != CodeCheck.Accepted)
        {
            LogCodeRefused(logger, check.ToString());
            return check == CodeCheck.Burned
                ? Problems.Of(StatusCodes.Status400BadRequest, "code-burned", "Too many wrong codes",
                    "This code can't be used any more: ask for a new one.")
                : Problems.Of(StatusCodes.Status400BadRequest, "invalid-code", "Wrong or expired code",
                    "Check the code in the latest email, or ask for a new one.");
        }

        db.ChangeTracker.Clear();
        var (user, created) = await SignIn.EmailAsync(db, email, now, cancellationToken);
        LogEmailSignedIn(logger, created);
        return TypedResults.Ok(await SignIn.StartSessionAsync(db, tokens, user, SignIn.EmailMethod, created, now, cancellationToken));
    }

    /// <summary>
    /// Signs in with a UPT Entra access token, checked exactly as the Entra bearer scheme checks one: the account of
    /// that UPT account, else (linked automatically) one with the same verified email, else a new one. 401
    /// <c>microsoft-token-invalid</c>; 503 <c>microsoft-unavailable</c> when UPT sign-in isn't configured or Microsoft's
    /// keys can't be loaded.
    /// </summary>
    internal static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> SignInWithMicrosoft(MicrosoftTokenRequest request,
        CampusPulseDbContext db, CampusPulseTokens tokens, EntraTokenValidator entra, TimeProvider time, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (!tokens.IsConfigured)
        {
            return SignInUnavailable();
        }

        var (check, principal) = await entra.ValidateAsync(request.AccessToken, cancellationToken);
        if (check != EntraCheck.Valid)
        {
            return MicrosoftProblem(check);
        }

        var now = time.GetUtcNow();
        var (user, created) = await SignIn.UptAsync(db, UptClaims.From(principal!), now, cancellationToken);
        LogUptSignedIn(loggers.CreateLogger(LogCategory), created);
        return TypedResults.Ok(await SignIn.StartSessionAsync(db, tokens, user, UserIdentity.UptMicrosoft, created, now, cancellationToken));
    }

    /// <summary>A new access token and a new refresh token for a refresh token, which stops working. 401
    /// <c>refresh-invalid</c> when it is unknown, expired, revoked or already used (then its whole session is revoked):
    /// the app signs out. Refresh one at a time: two refreshes with one token count as reuse.</summary>
    internal static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> Refresh(RefreshTokenRequest request,
        CampusPulseDbContext db, CampusPulseTokens tokens, TimeProvider time, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (!tokens.IsConfigured)
        {
            return SignInUnavailable();
        }

        var (result, response) = string.IsNullOrWhiteSpace(request.RefreshToken)
            ? (SignIn.RefreshResult.Invalid, (TokenResponse?)null)
            : await SignIn.RefreshAsync(db, tokens, request.RefreshToken.Trim(), time.GetUtcNow(), cancellationToken);
        if (result == SignIn.RefreshResult.Reused)
        {
            LogRefreshReused(loggers.CreateLogger(LogCategory));
        }

        return response is not null
            ? TypedResults.Ok(response)
            : Problems.Of(StatusCodes.Status401Unauthorized, "refresh-invalid", "Session ended", "Sign in again.");
    }

    /// <summary>Ends the refresh token's session (its whole family): 204, also for a token that is unknown or already
    /// revoked. The access token stays valid until it expires (15 min).</summary>
    internal static async Task<NoContent> SignOut(RefreshTokenRequest request, CampusPulseDbContext db, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            await SignIn.SignOutAsync(db, request.RefreshToken.Trim(), time.GetUtcNow(), cancellationToken);
        }

        return TypedResults.NoContent();
    }

    internal static ProblemHttpResult MicrosoftProblem(EntraCheck check) => check == EntraCheck.Unavailable
        ? Problems.Of(StatusCodes.Status503ServiceUnavailable, "microsoft-unavailable", "UPT sign-in unavailable",
            "Signing in with UPT doesn't work on this server right now. Try again later, or use your email.")
        : Problems.Of(StatusCodes.Status401Unauthorized, "microsoft-token-invalid", "Microsoft token refused",
            "The token isn't a UPT access token for this app.");

    /// <summary>The address's code row, locked for this transaction; with <paramref name="create"/>, made first when
    /// missing (two first requests at once can't both insert).</summary>
    private static async Task<EmailSignInCode?> LockCodeAsync(CampusPulseDbContext db, string email, bool create,
        CancellationToken cancellationToken)
    {
        if (create)
        {
            var none = Array.Empty<DateTimeOffset>();
            var epoch = DateTimeOffset.UnixEpoch;
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO email_sign_in_codes (email, expires_at, attempts, created_at, sends)
                VALUES ({email}, {epoch}, 0, {epoch}, {none})
                ON CONFLICT (email) DO NOTHING
                """, cancellationToken);
        }

        return (await db.EmailSignInCodes.FromSql($"SELECT * FROM email_sign_in_codes WHERE email = {email} FOR UPDATE")
            .ToListAsync(cancellationToken)).SingleOrDefault();
    }

    /// <summary>Takes back the code issued at <paramref name="issuedAt"/> and puts back the one it
    /// <paramref name="replaced"/>, unless a newer code replaced it meanwhile.</summary>
    private static async Task WithdrawAsync(CampusPulseDbContext db, string email, DateTimeOffset issuedAt,
        EmailSignInCode.Replaced replaced, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockCodeAsync(db, email, create: false, cancellationToken) is { } row && row.IssuedAt(issuedAt))
        {
            row.Withdraw(issuedAt, replaced);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static ProblemHttpResult InvalidEmail() => Problems.Of(StatusCodes.Status400BadRequest, "invalid-email",
        "Not an email address", "email must be a plain address like name@example.com.");

    private static ProblemHttpResult EmailUnavailable(string detail) =>
        Problems.Of(StatusCodes.Status503ServiceUnavailable, "email-unavailable", "Email sign-in unavailable", detail);

    private static ProblemHttpResult SignInUnavailable() => Problems.Of(StatusCodes.Status503ServiceUnavailable, "sign-in-unavailable",
        "Sign-in unavailable", "This server can't issue sessions (Auth__SigningKey isn't set).");

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Email sign-in code sent")]
    private static partial void LogCodeSent(ILogger logger);

    [LoggerMessage(EventId = 108, Level = LogLevel.Error, Message = "The email sender failed: {Reason}\n{StackTrace}")]
    private static partial void LogSenderFailed(ILogger logger, string reason, string? stackTrace);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "Email sign-in code not delivered (the code was withdrawn)")]
    private static partial void LogCodeNotDelivered(ILogger logger);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "Email sign-in code refused: {Outcome}")]
    private static partial void LogCodeRefused(ILogger logger, string outcome);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "Signed in with an email code (new account: {NewAccount})")]
    private static partial void LogEmailSignedIn(ILogger logger, bool newAccount);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information, Message = "Signed in with UPT (new account: {NewAccount})")]
    private static partial void LogUptSignedIn(ILogger logger, bool newAccount);

    [LoggerMessage(EventId = 105, Level = LogLevel.Warning, Message = "A refresh token was used again: its session was revoked")]
    private static partial void LogRefreshReused(ILogger logger);
}
