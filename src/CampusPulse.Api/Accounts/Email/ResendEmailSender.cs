using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CampusPulse.Api.Accounts.Email;

/// <summary>The <c>Email</c> configuration section.</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>Resend's API key; sent as a bearer token. Unset: no emails (sign-in by email answers 503), except the
    /// logging sender in Development.</summary>
    public string? ResendApiKey { get; set; }

    /// <summary>The sender, with a name; its domain must be verified at Resend.</summary>
    public string From { get; set; } = "CampusPulse <no-reply@campuspulse.thorsp.net>";
}

/// <summary>
/// Sends through Resend (<c>POST https://api.resend.com/emails</c>), a typed HttpClient with a 15 s timeout. Like the
/// push client it sets no proxy of its own, so on the Pi it goes through the egress proxy (<c>api.resend.com</c> is on
/// its allow-list). Logs the outcome only, never the address, the subject or Resend's answer (which may quote them).
/// </summary>
public sealed partial class ResendEmailSender(HttpClient http, IOptions<EmailOptions> options, ILogger<ResendEmailSender> logger) : IEmailSender
{
    public static readonly Uri BaseAddress = new("https://api.resend.com/");
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public bool IsConfigured => true;

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("emails",
                new ResendEmail(options.Value.From, [message.To], message.Subject, message.Text, message.Html), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            LogRefused(logger, (int)response.StatusCode);
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, e.GetType().Name);
            return false;
        }
    }

    private sealed record ResendEmail(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] IReadOnlyList<string> To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("html")] string Html);

    [LoggerMessage(EventId = 90, Level = LogLevel.Warning, Message = "Resend refused an email: HTTP {Status}")]
    private static partial void LogRefused(ILogger logger, int status);

    [LoggerMessage(EventId = 91, Level = LogLevel.Warning, Message = "Resend couldn't be reached: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);
}
