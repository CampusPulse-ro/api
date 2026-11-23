using System.Net;
using System.Text.Json;
using CampusPulse.Api.Accounts.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CampusPulse.Api.Tests;

/// <summary>The Resend sender against a stand-in for Resend's API.</summary>
public sealed class ResendEmailSenderTests
{
    private sealed class StubResend(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, JsonElement Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request, JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone()));
            return new HttpResponseMessage(status) { Content = new StringContent("""{ "id": "e-1" }""") };
        }
    }

    private static ResendEmailSender Sender(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = ResendEmailSender.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "re_test");
        return new ResendEmailSender(client, Options.Create(new EmailOptions()), NullLogger<ResendEmailSender>.Instance);
    }

    [Fact]
    public async Task It_posts_the_message_to_Resend_from_CampusPulse()
    {
        var resend = new StubResend(HttpStatusCode.OK);

        var sent = await Sender(resend).SendAsync(SignInEmail.Compose("ana@example.com", "123456", SignInLanguage.En), default);

        Assert.True(sent);
        var (request, body) = Assert.Single(resend.Calls);
        Assert.Equal((HttpMethod.Post, "https://api.resend.com/emails"), (request.Method, request.RequestUri!.ToString()));
        Assert.Equal("Bearer re_test", request.Headers.Authorization!.ToString());
        Assert.Equal("CampusPulse <no-reply@campuspulse.thorsp.net>", body.GetProperty("from").GetString());
        Assert.Equal(["ana@example.com"], body.GetProperty("to").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("Your CampusPulse code: 123456", body.GetProperty("subject").GetString());
        Assert.Contains("123456", body.GetProperty("text").GetString());
        Assert.Contains("123456", body.GetProperty("html").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refusal_is_not_sent(HttpStatusCode status) =>
        Assert.False(await Sender(new StubResend(status)).SendAsync(SignInEmail.Compose("ana@example.com", "123456", SignInLanguage.Ro), default));

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }

    [Fact]
    public async Task An_unreachable_Resend_is_not_sent() =>
        Assert.False(await Sender(new Unreachable()).SendAsync(SignInEmail.Compose("ana@example.com", "123456", SignInLanguage.En), default));
}

/// <summary>Which sender the app picks, and how it sets Resend's client up, without a database.</summary>
public sealed class EmailSetupTests
{
    /// <summary>Like the push client, the Resend client keeps the default proxy (<c>HTTPS_PROXY</c> on Linux): that's
    /// how it reaches Resend through the Pi's egress proxy.</summary>
    [Fact]
    public void With_a_key_it_sends_through_Resend_by_the_default_proxy()
    {
        using var factory = new CampusPulseAppFactory();
        using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("Email:ResendApiKey", " re_test_key "));

        Assert.IsType<ResendEmailSender>(app.Services.CreateScope().ServiceProvider.GetRequiredService<IEmailSender>());
        var handler = app.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(IEmailSender));
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var (useProxy, proxy) = handler switch
        {
            HttpClientHandler h => (h.UseProxy, h.Proxy),
            SocketsHttpHandler s => (s.UseProxy, s.Proxy),
            _ => throw new InvalidOperationException($"Unexpected primary handler {handler.GetType()}"),
        };
        Assert.True(useProxy);
        Assert.Null(proxy);

        var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IEmailSender));
        Assert.Equal(ResendEmailSender.Timeout, client.Timeout);
        Assert.Equal(ResendEmailSender.BaseAddress, client.BaseAddress);
        Assert.Equal("Bearer re_test_key", client.DefaultRequestHeaders.Authorization!.ToString());
    }

    [Theory]
    [InlineData("Testing", typeof(UnavailableEmailSender))]
    [InlineData("Production", typeof(UnavailableEmailSender))]
    [InlineData("Development", typeof(LoggingEmailSender))]
    public void Without_a_key_only_Development_has_a_sender(string environment, Type expected)
    {
        using var factory = new CampusPulseAppFactory();
        using var app = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));

        var sender = app.Services.CreateScope().ServiceProvider.GetRequiredService<IEmailSender>();

        Assert.IsType(expected, sender);
        Assert.Equal(expected == typeof(LoggingEmailSender), sender.IsConfigured);
    }
}
