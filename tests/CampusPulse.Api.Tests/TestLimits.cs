using Microsoft.AspNetCore.Hosting;

namespace CampusPulse.Api.Tests;

/// <summary>
/// Settings of the shared test factories. The rate limits stay on (every test goes through them) but high enough for
/// the tests that fire bursts on purpose (ten concurrent <c>DELETE /me</c>, fourteen device registrations); the limits
/// themselves are tested in <see cref="RateLimitingTests"/> with small ones. The timetable cache checks for a new import
/// on every read, so a test that imports sees it at once; the interval itself is tested in
/// <see cref="TimetableCacheTests"/>.
/// </summary>
public static class TestLimits
{
    public static void Configure(IWebHostBuilder builder)
    {
        foreach (var bucket in new[] { "SignedIn", "Anonymous", "Writes", "Account" })
        {
            builder.UseSetting($"RateLimits:{bucket}:Burst", "10000");
            builder.UseSetting($"RateLimits:{bucket}:PerMinute", "10000");
        }

        builder.UseSetting("TimetableCache:VersionCheckSeconds", "0");
    }
}
