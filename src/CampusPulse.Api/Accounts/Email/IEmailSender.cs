namespace CampusPulse.Api.Accounts.Email;

/// <summary>One email: to one address, plain text plus a minimal HTML version of the same.</summary>
public sealed record EmailMessage(string To, string Subject, string Text, string Html);

/// <summary>
/// Sends the API's emails (today only sign-in codes). <see cref="ResendEmailSender"/> in production, a logging sender in
/// Development, a capturing one in tests. Without one (<see cref="IsConfigured"/> false), sign-in by email answers 503.
/// </summary>
public interface IEmailSender
{
    bool IsConfigured { get; }

    /// <summary>True when the provider took the message.</summary>
    Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>No sender configured (no <c>Email:ResendApiKey</c> outside Development).</summary>
public sealed class UnavailableEmailSender : IEmailSender
{
    public bool IsConfigured => false;

    public Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>Development only: writes the message to the log instead of sending it, so a local sign-in can read its code
/// there. Never registered in another environment.</summary>
public sealed partial class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public bool IsConfigured => true;

    public Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        LogMessage(logger, message.Subject, message.Text);
        return Task.FromResult(true);
    }

    [LoggerMessage(EventId = 92, Level = LogLevel.Warning, Message = "Development: email not sent. {Subject}\n{Text}")]
    private static partial void LogMessage(ILogger logger, string subject, string text);
}
