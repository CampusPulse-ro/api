using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace CampusPulse.Api.Accounts.Email;

public static class EmailSetup
{
    /// <summary>The sender: Resend when <c>Email:ResendApiKey</c> is set; else the logging sender in Development and
    /// none elsewhere.</summary>
    public static WebApplicationBuilder AddCampusPulseEmail(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.Section));
        if (!string.IsNullOrWhiteSpace(builder.Configuration["Email:ResendApiKey"]))
        {
            builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>((provider, client) =>
            {
                client.BaseAddress = ResendEmailSender.BaseAddress;
                client.Timeout = ResendEmailSender.Timeout;
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                    provider.GetRequiredService<IOptions<EmailOptions>>().Value.ResendApiKey!.Trim());
            });
        }
        else if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }
        else
        {
            builder.Services.AddSingleton<IEmailSender, UnavailableEmailSender>();
        }

        return builder;
    }
}
