using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CampusPulse.Api.Accounts.Email;

namespace CampusPulse.Api.Tests;

/// <summary>Stands in for Resend: keeps every message, and can be told to fail (<see cref="Refuse"/>) or to throw
/// (<see cref="Throw"/>).</summary>
public sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public bool IsConfigured { get; set; } = true;

    public bool Refuse { get; set; }

    public bool Throw { get; set; }

    public IReadOnlyList<EmailMessage> Sent => [.. _sent];

    public Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Refuse)
        {
            return Task.FromResult(false);
        }

        if (Throw)
        {
            throw new InvalidOperationException("The email sender broke.");
        }

        _sent.Enqueue(message);
        return Task.FromResult(true);
    }

    /// <summary>The code of the latest message to <paramref name="email"/>.</summary>
    public string CodeFor(string email) => Code(_sent.Last(m => m.To == email.Trim().ToLowerInvariant()));

    public static string Code(EmailMessage message) => SixDigits().Match(message.Text).Value;

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex SixDigits();
}
