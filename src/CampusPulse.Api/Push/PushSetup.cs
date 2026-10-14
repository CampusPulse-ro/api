using System.Net.Http.Headers;

namespace CampusPulse.Api.Push;

public static class PushSetup
{
    /// <summary>
    /// Push notifications through Expo. The sender is a typed HttpClient with a timeout, and sets no
    /// proxy of its own: it uses <see cref="HttpClient.DefaultProxy"/>, which on Linux comes from
    /// <c>HTTPS_PROXY</c> / <c>NO_PROXY</c>, so on the Pi it goes through the egress proxy like the
    /// sign-in metadata does (a test checks the handler keeps the default proxy). With
    /// <c>Push:Expo:AccessToken</c> set, requests carry it as a bearer token (needed once "enhanced
    /// push security" is on for the Expo project).
    /// </summary>
    public static IServiceCollection AddCampusPulsePush(this IServiceCollection services)
    {
        services.AddHttpClient<IPushSender, ExpoPushSender>((provider, client) =>
        {
            client.BaseAddress = ExpoPushSender.BaseAddress;
            client.Timeout = ExpoPushSender.Timeout;
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var accessToken = provider.GetRequiredService<IConfiguration>()["Push:Expo:AccessToken"];
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
            }
        });
        services.AddScoped<PushDelivery>();
        services.AddSingleton<TestNotificationLimit>();
        services.AddSingleton<PushReceiptChecker>();
        services.AddHostedService(provider => provider.GetRequiredService<PushReceiptChecker>());
        return services;
    }
}
