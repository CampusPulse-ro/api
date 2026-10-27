using CampusPulse.Domain.Reminders;

namespace CampusPulse.Domain.Tests;

public sealed class ClassReminderTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void The_offered_leads_are_allowed(int minutes)
    {
        Assert.True(ReminderSettings.IsAllowed(minutes));
        Assert.Equal(minutes, new ReminderSettings(Guid.NewGuid(), minutes, Now).MinutesBefore);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(61)]
    [InlineData(1440)]
    public void Any_other_lead_is_refused(int minutes)
    {
        Assert.False(ReminderSettings.IsAllowed(minutes));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReminderSettings(Guid.NewGuid(), minutes, Now));
    }

    [Fact]
    public void The_longest_lead_is_an_hour() => Assert.Equal(60, ReminderSettings.MaxMinutesBefore);
}
