using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace CampusPulse.Api.Auth;

/// <summary>
/// Loads the tenant's sign-in metadata (issuer and signing keys) once at startup, in the background,
/// so a server that can't reach Microsoft says so in the log at once, rather than through the first
/// student's 401. On the Pi the API reaches login.microsoftonline.com only through the egress proxy
/// (<c>HTTPS_PROXY</c>). The app starts either way; JwtBearer retries on later requests, and until
/// it succeeds a request with a token answers 500 (a server fault, not a bad token).
/// </summary>
public sealed partial class AuthMetadataCheck(IOptionsMonitor<JwtBearerOptions> options, ILoggerFactory loggers) : BackgroundService
{
    private readonly ILogger _logger = loggers.CreateLogger(AuthSetup.LogCategory);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bearer = options.Get(JwtBearerDefaults.AuthenticationScheme);
        try
        {
            var configuration = await bearer.ConfigurationManager!.GetConfigurationAsync(stoppingToken);
            LogLoaded(_logger, bearer.MetadataAddress ?? bearer.Authority ?? "(static)", configuration.SigningKeys.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            var cause = e.GetBaseException();
            LogUnreachable(_logger, bearer.MetadataAddress ?? bearer.Authority ?? "(static)", e.GetType().Name,
                $"{cause.GetType().Name}: {cause.Message}");
        }
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Sign-in metadata loaded from {MetadataAddress} ({KeyCount} signing keys)")]
    private static partial void LogLoaded(ILogger logger, string metadataAddress, int keyCount);

    [LoggerMessage(EventId = 21, Level = LogLevel.Error,
        Message = "Sign-in metadata could not be loaded from {MetadataAddress} ({ExceptionType}; cause {Cause}). Requests with a token answer 500 until it loads. On the Pi, check HTTPS_PROXY and the egress proxy's allow list.")]
    private static partial void LogUnreachable(ILogger logger, string metadataAddress, string exceptionType, string cause);
}
