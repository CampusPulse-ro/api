using CampusPulse.Domain.Users;

namespace CampusPulse.Domain.Tests;

public sealed class UserTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_user_is_seen_now()
    {
        var user = new User("oid-1", " ana@student.upt.ro ", "Ana", T0);

        Assert.Equal("ana@student.upt.ro", user.Email);
        Assert.Equal(T0, user.CreatedAt);
        Assert.Equal(T0, user.LastSeenAt);
        Assert.NotEqual(Guid.Empty, user.Id);
    }

    [Fact]
    public void Signing_in_again_within_five_minutes_changes_nothing()
    {
        var user = new User("oid-1", "ana@student.upt.ro", "Ana", T0);

        Assert.False(user.Seen("ana@student.upt.ro", "Ana", T0.AddMinutes(4)));
        Assert.Equal(T0, user.LastSeenAt);
    }

    [Fact]
    public void After_five_minutes_last_seen_moves()
    {
        var user = new User("oid-1", "ana@student.upt.ro", "Ana", T0);

        Assert.True(user.Seen("ana@student.upt.ro", "Ana", T0.AddMinutes(5)));
        Assert.Equal(T0.AddMinutes(5), user.LastSeenAt);
    }

    [Fact]
    public void A_changed_name_or_email_is_saved_at_once()
    {
        var user = new User("oid-1", "ana@student.upt.ro", "Ana", T0);

        Assert.True(user.Seen("ana.pop@student.upt.ro", "Ana Pop", T0.AddSeconds(1)));
        Assert.Equal("ana.pop@student.upt.ro", user.Email);
        Assert.Equal("Ana Pop", user.DisplayName);
        Assert.Equal(T0, user.LastSeenAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_token_without_name_or_email_keeps_the_stored_ones(string? missing)
    {
        var user = new User("oid-1", "ana@student.upt.ro", "Ana", T0);

        Assert.False(user.Seen(missing, missing, T0.AddSeconds(1)));
        Assert.Equal("ana@student.upt.ro", user.Email);
        Assert.Equal("Ana", user.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_object_id_is_required(string oid) =>
        Assert.Throws<ArgumentException>(() => new User(oid, "a@b.c", "A", T0));

    [Fact]
    public void Overlong_values_are_cut_to_the_column_size()
    {
        var user = new User("oid-1", new string('e', 400), new string('n', 300), T0);

        Assert.Equal(User.EmailMaxLength, user.Email.Length);
        Assert.Equal(User.DisplayNameMaxLength, user.DisplayName.Length);
    }
}
