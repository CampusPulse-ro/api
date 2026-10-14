using CampusPulse.Domain.Push;

namespace CampusPulse.Domain.Tests;

public sealed class PushDeviceTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]")]
    [InlineData("ExpoPushToken[abc-DEF_123]")]
    public void Expo_tokens_are_accepted(string token) => Assert.True(PushDevice.IsExpoToken(token));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ExponentPushToken[]")]
    [InlineData("ExponentPushToken[abc]\n")]
    [InlineData(" ExponentPushToken[abc]")]
    [InlineData("ExponentPushToken[a.b]")]
    [InlineData("exponentpushtoken[abc]")]
    [InlineData("ExponentPushToken[abc]ExponentPushToken[def]")]
    public void Anything_else_is_refused(string? token) => Assert.False(PushDevice.IsExpoToken(token));

    [Fact]
    public void A_token_over_the_column_limit_is_refused()
    {
        var inside = new string('a', PushDevice.TokenMaxLength - "ExponentPushToken[]".Length);
        Assert.True(PushDevice.IsExpoToken($"ExponentPushToken[{inside}]"));
        Assert.False(PushDevice.IsExpoToken($"ExponentPushToken[{inside}a]"));
    }

    [Fact]
    public void A_device_can_only_be_made_from_an_expo_token() =>
        Assert.Throws<ArgumentException>(() => new PushDevice(Guid.NewGuid(), "not-a-token", PushPlatform.Android, Now));

    [Fact]
    public void Registering_again_moves_the_device_and_refreshes_its_time_but_not_its_creation()
    {
        var device = new PushDevice(Guid.NewGuid(), "ExponentPushToken[abc]", PushPlatform.Android, Now);
        var other = Guid.NewGuid();

        device.Register(other, PushPlatform.Ios, Now.AddDays(1));

        Assert.Equal(other, device.UserId);
        Assert.Equal(PushPlatform.Ios, device.Platform);
        Assert.Equal(Now, device.CreatedAt);
        Assert.Equal(Now.AddDays(1), device.LastRegisteredAt);
    }
}
