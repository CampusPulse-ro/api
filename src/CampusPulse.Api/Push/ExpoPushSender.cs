using System.Net.Http.Json;
using System.Text.Json;
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
        var body = batch.Select(m => new ExpoMessage(m.To, m.Title, m.Body, "default")).ToArray();
        ExpoResponse<List<ExpoTicket>>? response;
        try
        {
            using var http = await client.PostAsJsonAsync("--/api/v2/push/send", body, Json, cancellationToken);
            if (!http.IsSuccessStatusCode)
            {
                LogRequestFailed(logger, "send", (int)http.StatusCode);
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
            return Refused(batch, PushErrors.RequestFailed);
        }
        catch (JsonException)
        {
            return Refused(batch, PushErrors.MalformedResponse);
        }

        if (response?.Data is not { } data || data.Count != batch.Length)
        {
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
                    throw new PushServiceException($"getReceipts answered HTTP {(int)http.StatusCode}.");
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
            catch (JsonException)
            {
                throw new PushServiceException("getReceipts answered something unreadable.");
            }

            if (response?.Data is not { } data)
            {
                throw new PushServiceException("getReceipts answered without data.");
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

    /// <summary>Expo's codes are single words (<c>DeviceNotRegistered</c>); anything else becomes
    /// <see cref="PushErrors.Unknown"/>, so whatever lands in a log line is a known shape.</summary>
    private static string ErrorCode(ExpoDetails? details) =>
        details?.Error is { Length: > 0 and <= 64 } code && code.All(char.IsAsciiLetter) ? code : PushErrors.Unknown;

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "Expo push {Operation} answered HTTP {StatusCode}")]
    private static partial void LogRequestFailed(ILogger logger, string operation, int statusCode);

    [LoggerMessage(EventId = 31, Level = LogLevel.Warning, Message = "Expo push {Operation} unreachable: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string operation, string reason);

    private sealed record ExpoMessage(string To, string Title, string Body, string Sound);

    private sealed record ExpoResponse<T>(T? Data);

    private sealed record ExpoTicket(string? Status, string? Id, ExpoDetails? Details);

    private sealed record ExpoReceipt(string? Status, ExpoDetails? Details);

    private sealed record ExpoDetails(string? Error);
}
