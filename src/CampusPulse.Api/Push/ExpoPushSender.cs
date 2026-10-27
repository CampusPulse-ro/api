using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampusPulse.Domain.Push;

namespace CampusPulse.Api.Push;

/// <summary>
/// Expo's push API (<c>https://exp.host/--/api/v2/push/send</c> and <c>…/getReceipts</c>), as a typed
/// HttpClient (<see cref="PushSetup"/>). Messages go in batches of at most 100 and receipt reads in
/// batches of 300, Expo's limits. Nothing here logs a token: Expo's error <c>message</c> texts quote
/// the token, so only the error code (<c>details.error</c>) is ever kept.
/// </summary>
public sealed partial class ExpoPushSender(HttpClient client, ILogger<ExpoPushSender> logger) : IPushSender
{
    public const int SendBatchSize = 100;
    public const int ReceiptBatchSize = 300;
    public static readonly Uri BaseAddress = new("https://exp.host/");
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PushTicket>> SendAsync(IReadOnlyList<PushMessage> messages, CancellationToken cancellationToken)
    {
        var tickets = new List<PushTicket>(messages.Count);
        foreach (var batch in messages.Chunk(SendBatchSize))
        {
            tickets.AddRange(await SendBatchAsync(batch, cancellationToken));
        }

        return tickets;
    }

    private async Task<IEnumerable<PushTicket>> SendBatchAsync(PushMessage[] batch, CancellationToken cancellationToken)
    {
        var body = batch.Select(m => new ExpoMessage(m.To, m.Title, m.Body, "default", m.Data, m.HighPriority ? "high" : null,
            m.TimeToLive is { } ttl ? Math.Max(1, (int)Math.Ceiling(ttl.TotalSeconds)) : null)).ToArray();
        ExpoResponse<List<ExpoTicket>>? response;
        try
        {
            using var http = await client.PostAsJsonAsync("--/api/v2/push/send", body, Json, cancellationToken);
            if (!http.IsSuccessStatusCode)
            {
                LogRequestFailed(logger, "send", (int)http.StatusCode, await ErrorCodesAsync(http, cancellationToken));
                return Refused(batch, PushErrors.RequestFailed);
            }

            response = await http.Content.ReadFromJsonAsync<ExpoResponse<List<ExpoTicket>>>(Json, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            LogUnreachable(logger, "send", e.GetType().Name);
            return Refused(batch, PushErrors.RequestFailed);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, "send", "timeout");
            return Refused(batch, PushErrors.TimedOut);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return Refused(batch, PushErrors.MalformedResponse);
        }

        if (response?.Data is not { } data || data.Count != batch.Length)
        {
            if (response?.Errors is { Count: > 0 } errors)
            {
                LogRequestFailed(logger, "send", 200, Codes(errors));
                return Refused(batch, PushErrors.RequestFailed);
            }

            return Refused(batch, PushErrors.MalformedResponse);
        }

        return data.Select(ticket => ticket is { Status: "ok", Id: { Length: > 0 and <= PushReceiptPending.TicketIdMaxLength } id }
            ? new PushTicket(id, null)
            : new PushTicket(null, ErrorCode(ticket?.Details)));
    }

    public async Task<IReadOnlyDictionary<string, PushReceipt>> GetReceiptsAsync(IReadOnlyList<string> ticketIds,
        CancellationToken cancellationToken)
    {
        var receipts = new Dictionary<string, PushReceipt>(StringComparer.Ordinal);
        foreach (var batch in ticketIds.Chunk(ReceiptBatchSize))
        {
            ExpoResponse<Dictionary<string, ExpoReceipt>>? response;
            try
            {
                using var http = await client.PostAsJsonAsync("--/api/v2/push/getReceipts", new { ids = batch }, Json, cancellationToken);
                if (!http.IsSuccessStatusCode)
                {
                    throw new PushServiceException(
                        $"getReceipts answered HTTP {(int)http.StatusCode} ({await ErrorCodesAsync(http, cancellationToken)}).");
                }

                response = await http.Content.ReadFromJsonAsync<ExpoResponse<Dictionary<string, ExpoReceipt>>>(Json, cancellationToken);
            }
            catch (HttpRequestException e)
            {
                throw new PushServiceException($"getReceipts unreachable ({e.GetType().Name}).");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PushServiceException("getReceipts timed out.");
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                throw new PushServiceException("getReceipts answered something unreadable.");
            }

            if (response?.Data is not { } data)
            {
                throw new PushServiceException(response?.Errors is { Count: > 0 } errors
                    ? $"getReceipts answered errors ({Codes(errors)})."
                    : "getReceipts answered without data.");
            }

            foreach (var id in batch)
            {
                if (data.TryGetValue(id, out var receipt) && receipt is not null)
                {
                    receipts[id] = new PushReceipt(receipt.Status == "ok" ? null : ErrorCode(receipt.Details));
                }
            }
        }

        return receipts;
    }

    private static IEnumerable<PushTicket> Refused(PushMessage[] batch, string error) =>
        batch.Select(_ => new PushTicket(null, error));

    private static string ErrorCode(ExpoDetails? details) => SafeCode(details?.Error);

    /// <summary>Expo's codes are words in ASCII letters and underscores (<c>DeviceNotRegistered</c>,
    /// <c>PUSH_TOO_MANY_EXPERIENCE_IDS</c>); anything else becomes <see cref="PushErrors.Unknown"/>, so
    /// whatever lands in a log line has a known shape. A token can't pass: it has brackets.</summary>
    private static string SafeCode(string? code) =>
        code is { Length: > 0 and <= 64 } && code.All(c => char.IsAsciiLetter(c) || c == '_') ? code : PushErrors.Unknown;

    private static string Codes(IEnumerable<ExpoError?> errors) =>
        string.Join(",", errors.Select(e => SafeCode(e?.Code)).Distinct(StringComparer.Ordinal));

    /// <summary>The request-level error codes of a failed answer (<c>errors[].code</c>). Only the codes:
    /// an error's <c>message</c> and <c>details</c> can list the tokens of the request.</summary>
    private static async Task<string> ErrorCodesAsync(HttpResponseMessage http, CancellationToken cancellationToken)
    {
        try
        {
            var body = await http.Content.ReadFromJsonAsync<ExpoResponse<JsonElement>>(Json, cancellationToken);
            return body?.Errors is { Count: > 0 } errors ? Codes(errors) : "no error codes";
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or HttpRequestException)
        {
            return "no error codes";
        }
    }

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "Expo push {Operation} answered HTTP {StatusCode}: {ErrorCodes}")]
    private static partial void LogRequestFailed(ILogger logger, string operation, int statusCode, string errorCodes);

    [LoggerMessage(EventId = 31, Level = LogLevel.Warning, Message = "Expo push {Operation} unreachable: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string operation, string reason);

    /// <summary>One message as Expo reads it. The optional fields are left out when not set: Expo then applies each
    /// platform's default (normal priority on Android, four weeks to live).</summary>
    private sealed record ExpoMessage(
        string To,
        string Title,
        string Body,
        string Sound,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Data,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Priority,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Ttl);

    private sealed record ExpoResponse<T>(T? Data, List<ExpoError?>? Errors);

    private sealed record ExpoError(string? Code);

    private sealed record ExpoTicket(string? Status, string? Id, ExpoDetails? Details);

    private sealed record ExpoReceipt(string? Status, ExpoDetails? Details);

    private sealed record ExpoDetails(string? Error);
}
