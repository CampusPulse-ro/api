using CampusPulse.Domain.Deadlines;
using CampusPulse.Domain.Users;

namespace CampusPulse.Domain.Tests;

public sealed class EmailSignInCodeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_code_is_six_digits_and_kept_only_as_a_salted_hash()
    {
        var row = new EmailSignInCode("Ana@Example.com");
        var code = EmailSignInCode.NewCode();

        row.Issue(code, T0);

        Assert.Matches("^[0-9]{6}$", code);
        Assert.Equal("ana@example.com", row.Email);
        Assert.Equal((32, 16), (row.CodeHash!.Length, row.Salt!.Length));
        Assert.DoesNotContain(code, Convert.ToHexString(row.CodeHash), StringComparison.Ordinal);
        Assert.Equal(T0 + TimeSpan.FromMinutes(10), row.ExpiresAt);
    }

    [Fact]
    public void The_right_code_works_once()
    {
        var row = Issued("123456");

        Assert.Equal(CodeCheck.Accepted, row.Check("123456", T0.AddMinutes(9)));
        Assert.Equal(CodeCheck.Invalid, row.Check("123456", T0.AddMinutes(9)));
    }

    [Fact]
    public void An_expired_code_is_invalid() => Assert.Equal(CodeCheck.Invalid, Issued("123456").Check("123456", T0.AddMinutes(10)));

    [Fact]
    public void Five_wrong_attempts_burn_the_code_even_for_the_right_one()
    {
        var row = Issued("123456");

        var results = Enumerable.Range(0, 5).Select(_ => row.Check("000000", T0.AddMinutes(1))).ToList();

        Assert.Equal([CodeCheck.Invalid, CodeCheck.Invalid, CodeCheck.Invalid, CodeCheck.Invalid, CodeCheck.Burned], results);
        Assert.Equal(CodeCheck.Burned, row.Check("123456", T0.AddMinutes(1)));
    }

    [Fact]
    public void A_new_code_replaces_the_last_and_resets_the_attempts()
    {
        var row = Issued("123456");
        row.Check("000000", T0);

        row.Issue("654321", T0.AddMinutes(1));

        Assert.Equal(0, row.Attempts);
        Assert.Equal(CodeCheck.Invalid, row.Check("123456", T0.AddMinutes(2)));
        Assert.Equal(CodeCheck.Accepted, row.Check("654321", T0.AddMinutes(2)));
    }

    [Fact]
    public void An_address_gets_three_codes_per_15_minutes()
    {
        var row = new EmailSignInCode("ana@example.com");
        foreach (var minute in new[] { 0, 1, 2 })
        {
            Assert.Null(row.SendRefusal(T0.AddMinutes(minute)));
            row.Issue("111111", T0.AddMinutes(minute));
        }

        Assert.Equal(TimeSpan.FromMinutes(12), row.SendRefusal(T0.AddMinutes(3)));
        Assert.Null(row.SendRefusal(T0.AddMinutes(15)));
    }

    [Fact]
    public void An_address_gets_ten_codes_a_day()
    {
        var row = new EmailSignInCode("ana@example.com");
        for (var i = 0; i < 10; i++)
        {
            row.Issue("111111", T0.AddMinutes(20 * i));
        }

        var refusal = row.SendRefusal(T0.AddMinutes(200));

        Assert.Equal(TimeSpan.FromDays(1) - TimeSpan.FromMinutes(200), refusal);
        Assert.Null(row.SendRefusal(T0.AddDays(1)));
    }

    [Fact]
    public void A_withdrawn_code_doesnt_count_and_doesnt_work()
    {
        var row = Issued("123456");

        row.Withdraw(T0);

        Assert.Empty(row.Sends);
        Assert.Equal(CodeCheck.Invalid, row.Check("123456", T0.AddMinutes(1)));
    }

    private static EmailSignInCode Issued(string code)
    {
        var row = new EmailSignInCode("ana@example.com");
        row.Issue(code, T0);
        return row;
    }
}

public sealed class RefreshTokenTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_token_is_256_random_bits_kept_as_its_hash_for_60_days()
    {
        var (row, token) = RefreshToken.Start(Guid.NewGuid(), "email", T0);

        Assert.Equal(43, token.Length);   // 32 bytes, base64url without padding
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
        Assert.Equal(RefreshToken.HashOf(token), row.TokenHash);
        Assert.NotEqual(token, row.TokenHash);
        Assert.Equal(T0.AddDays(60), row.ExpiresAt);
        Assert.True(row.Usable(T0.AddDays(59)));
        Assert.False(row.Usable(T0.AddDays(60)));
    }

    [Fact]
    public void Rotating_revokes_the_token_and_gives_the_next_of_its_family()
    {
        var (row, _) = RefreshToken.Start(Guid.NewGuid(), "upt-microsoft", T0);

        var (next, nextToken) = row.Rotate(T0.AddDays(1));

        Assert.False(row.Usable(T0.AddDays(1)));
        Assert.Equal(next.TokenHash, row.ReplacedByHash);
        Assert.Equal((row.FamilyId, row.UserId, "upt-microsoft"), (next.FamilyId, next.UserId, next.Method));
        Assert.Equal(RefreshToken.HashOf(nextToken), next.TokenHash);
        Assert.Equal(T0.AddDays(61), next.ExpiresAt);
    }
}

public sealed class DeadlineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_deadline_is_trimmed_and_its_empty_parts_are_none()
    {
        var deadline = new Deadline(Guid.NewGuid(), "  Lab 3  ", " ", new DateTimeOffset(2026, 11, 30, 23, 59, 0, TimeSpan.FromHours(2)), "", T0);

        Assert.Equal(("Lab 3", (string?)null, (string?)null, false), (deadline.Title, deadline.Course, deadline.Notes, deadline.Done));
        Assert.Equal(TimeSpan.Zero, deadline.DueAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 30, 21, 59, 0, TimeSpan.Zero), deadline.DueAt);
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("   ", null, null)]
    [InlineData(null, null, null)]
    public void A_title_is_required(string? title, string? course, string? notes) =>
        Assert.NotNull(Deadline.Problem(title, course, notes));

    [Fact]
    public void Limits_are_200_200_and_2000()
    {
        Assert.Null(Deadline.Problem(new string('t', 200), new string('c', 200), new string('n', 2000)));
        Assert.NotNull(Deadline.Problem(new string('t', 201), null, null));
        Assert.NotNull(Deadline.Problem("t", new string('c', 201), null));
        Assert.NotNull(Deadline.Problem("t", null, new string('n', 2001)));
    }
}
