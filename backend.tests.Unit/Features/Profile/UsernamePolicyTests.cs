using backend.main.features.profile;
using backend.main.shared.exceptions.http;

using FluentAssertions;

namespace backend.tests.Unit.Features.Profile;

public class UsernamePolicyTests
{
    [Theory]
    [InlineData("  Mixed.Case  ", "mixed.case")]
    [InlineData("already-lower", "already-lower")]
    [InlineData("a-b_c.d", "a-b_c.d")]
    [InlineData("user22", "user22")]
    public void NormalizeAndValidate_ShouldTrimAndLowercase(string input, string expected)
    {
        UsernamePolicy.NormalizeAndValidate(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeAndValidate_ShouldRejectWhitespace()
    {
        var act = () => UsernamePolicy.NormalizeAndValidate("   ");

        act.Should().Throw<BadRequestException>()
            .WithMessage("Username is required.");
    }

    [Fact]
    public void NormalizeAndValidate_ShouldValidateTheNormalizedLength()
    {
        UsernamePolicy.NormalizeAndValidate($"  {new string('a', 50)}  ")
            .Should().HaveLength(50);

        var tooLong = () => UsernamePolicy.NormalizeAndValidate(new string('a', 51));
        tooLong.Should().Throw<BadRequestException>()
            .WithMessage("Username must be 50 characters or fewer.");

        var tooShort = () => UsernamePolicy.NormalizeAndValidate("ab");
        tooShort.Should().Throw<BadRequestException>()
            .WithMessage("Username must be at least 3 characters.");
    }

    [Theory]
    [InlineData("\u00c9V\u00c9NEMENT")] // non-ASCII survives Normalize but is not a legal username
    [InlineData("a b")]
    [InlineData("a@b")]
    [InlineData(".ab")]
    [InlineData("ab-")]
    [InlineData("a..b")]
    [InlineData("a._b")]
    [InlineData("a__b")]
    public void NormalizeAndValidate_ShouldRejectMalformedNames(string input)
    {
        var act = () => UsernamePolicy.NormalizeAndValidate(input);

        act.Should().Throw<BadRequestException>()
            .WithMessage(UsernamePolicy.FormatMessage);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("ADMIN")]
    [InlineData("  Support  ")]
    public void NormalizeAndValidate_ShouldRejectReservedNamesWithoutNamingTheList(string input)
    {
        var act = () => UsernamePolicy.NormalizeAndValidate(input);

        act.Should().Throw<BadRequestException>()
            .WithMessage("That username is not available.");
    }

    [Fact]
    public void ReservedNames_ShouldBePinnedSoChangesAreDeliberate()
    {
        UsernamePolicy.ReservedNames.Should().BeEquivalentTo([
            "admin", "administrator", "anonymous", "api", "moderator", "null", "official",
            "root", "security", "staff", "superuser", "support", "system", "undefined"
        ]);
    }

    /// <summary>
    /// The "new values only" guarantee. Rows written before the format rules existed - including
    /// everything the backfill migration derived from email local parts - still have to resolve on
    /// every lookup path, all of which go through Normalize.
    /// </summary>
    [Theory]
    [InlineData("\u00c9V\u00c9NEMENT", "\u00e9v\u00e9nement")]
    [InlineData("Legacy..Name", "legacy..name")]
    [InlineData(".ab", ".ab")]
    [InlineData("ab-", "ab-")]
    [InlineData("me", "me")]
    [InlineData("admin", "admin")]
    [InlineData("a b", "a b")]
    public void Normalize_ShouldNotApplyFormatRules(string input, string expected)
    {
        UsernamePolicy.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("a-b_c.d", true)]
    [InlineData("ab", false)]
    [InlineData("a..b", false)]
    [InlineData(".ab", false)]
    [InlineData("ab-", false)]
    [InlineData("a b", false)]
    public void IsWellFormed_ShouldNotThrowOnAnyInput(string normalized, bool expected)
    {
        UsernamePolicy.IsWellFormed(normalized).Should().Be(expected);
    }
}
