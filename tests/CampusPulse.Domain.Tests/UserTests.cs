using CampusPulse.Domain.Users;

namespace CampusPulse.Domain.Tests;

public sealed class UserTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_email_account_has_its_normalized_address_and_its_local_part_as_name()
    {
        var user = User.FromEmail(" Ana.Pop@Example.COM ", T0);

        Assert.Equal(("ana.pop@example.com", "ana.pop", (string?)null), (user.Email, user.DisplayName, user.EntraObjectId));
        Assert.Equal("ana.pop@example.com", user.LegacyEmail);
        Assert.Equal((T0, T0), (user.CreatedAt, user.LastSeenAt));
    }

    [Fact]
    public void A_UPT_account_keeps_its_oid_for_the_previous_release_and_may_have_no_email()
    {
        var user = User.FromUpt("oid-1", null, " Ana Pop ", T0);

        Assert.Equal(("oid-1", (string?)null, "Ana Pop"), (user.EntraObjectId, user.Email, user.DisplayName));
        Assert.Equal("", user.LegacyEmail);   // the previous release reads a non-null string
        user.UnlinkUpt();
        Assert.Null(user.EntraObjectId);
    }

    [Fact]
    public void An_email_is_set_once_and_never_changed()
    {
        var user = User.FromUpt("oid-1", null, "Ana", T0);

        user.VerifyEmail("Ana@Student.upt.ro");
        user.VerifyEmail("other@example.com");

        Assert.Equal(("ana@student.upt.ro", "ana@student.upt.ro"), (user.Email, user.LegacyEmail));
    }

    [Theory]
    [InlineData("ana@student.upt.ro", true)]
    [InlineData("prof@upt.ro", true)]
    [InlineData("x@cs.upt.ro", true)]
    [InlineData("ana@notupt.ro", false)]
    [InlineData("ana@upt.ro.example.com", false)]
    [InlineData("ana@gmail.com", false)]
    public void Only_UPT_domains_count_as_UPT_addresses(string email, bool upt) => Assert.Equal(upt, Emails.IsUpt(email));

    [Fact]
    public void Seen_saves_at_most_every_five_minutes_unless_the_name_changed()
    {
        var user = User.FromUpt("oid-1", "ana@student.upt.ro", "Ana", T0);

        Assert.False(user.Seen("Ana", T0.AddMinutes(4)));
        Assert.True(user.Seen(null, T0.AddMinutes(5)));
        Assert.True(user.Seen("Ana Pop", T0.AddMinutes(6)));
        Assert.Equal("Ana Pop", user.DisplayName);
        Assert.False(user.Seen("  ", T0.AddMinutes(7)));
        Assert.Equal("Ana Pop", user.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_UPT_account_needs_an_oid(string oid) =>
        Assert.Throws<ArgumentException>(() => User.FromUpt(oid, null, "A", T0));

    [Theory]
    [InlineData("ana@student.upt.ro", true)]
    [InlineData(" Ana@Example.com ", true)]
    [InlineData("a.b+tag@sub.example.ro", true)]
    [InlineData("ana", false)]
    [InlineData("ana@", false)]
    [InlineData("@example.com", false)]
    [InlineData("ana@example", false)]
    [InlineData("ana@@example.com", false)]
    [InlineData("Ana <ana@example.com>", false)]
    [InlineData("ana@exa mple.com", false)]
    [InlineData("ana@example.com.", false)]
    [InlineData(null, false)]
    public void Only_plain_addresses_are_emails(string? email, bool valid) => Assert.Equal(valid, Emails.IsValid(email));

    [Fact]
    public void An_address_longer_than_254_is_refused() =>
        Assert.False(Emails.IsValid(new string('a', 245) + "@example.com"));
}
