namespace CampusPulse.Api.Reminders;

public static class ReminderSetup
{
    /// <summary>Class reminders: the <c>Reminders</c> options (<c>Reminders__Enabled</c>, default true) and the sender,
    /// a singleton so tests can drive its ticks, registered as a hosted service. It sends through the push setup
    /// (<see cref="Push.PushDelivery"/>), so that comes first.</summary>
    public static IServiceCollection AddCampusPulseReminders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ReminderOptions>(configuration.GetSection(ReminderOptions.Section));
        services.AddSingleton<ClassReminderSender>();
        services.AddHostedService(provider => provider.GetRequiredService<ClassReminderSender>());
        return services;
    }
}
