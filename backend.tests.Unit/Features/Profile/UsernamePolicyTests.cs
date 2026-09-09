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

    /// <summary>
    /// Mixed case was always accepted — NormalizeAndValidate lowercases before it validates — so the
    /// display form captures what used to be thrown away rather than relaxing a rule.
    /// </summary>
    [Theory]
    [InlineData("ThomasT", "thomast", "ThomasT")]
    [InlineData("  SmartCat23  ", "smartcat23", "SmartCat23")]
    [InlineData("already-lower", "already-lower", "already-lower")]
    public void NormalizeAndValidateWithDisplay_ShouldKeepTheCasingAndLowercaseTheKey(
        string input,
        string expectedUsername,
        string expectedDisplay)
    {
        var forms = UsernamePolicy.NormalizeAndValidateWithDisplay(input);

        forms.Username.Should().Be(expectedUsername);
        forms.Display.Should().Be(expectedDisplay);
    }

    /// <summary>
    /// The invariant every write path has to establish, and the only thing that makes a display
    /// column safe to store beside a lookup key.
    /// </summary>
    [Theory]
    [InlineData("ThomasT")]
    [InlineData("SmartCat23")]
    [InlineData("a.b_c-d")]
    public void NormalizeAndValidateWithDisplay_ShouldProduceADisplayThatNormalizesToTheUsername(string input)
    {
        var forms = UsernamePolicy.NormalizeAndValidateWithDisplay(input);

        UsernamePolicy.Normalize(forms.Display).Should().Be(forms.Username);
        UsernamePolicy.IsValidDisplayFor(forms.Username, forms.Display).Should().BeTrue();
    }

    /// <summary>
    /// A display differing by anything but case is a corrupt row, not a rename.
    /// </summary>
    [Theory]
    [InlineData("thomast", "ThomasT", true)]
    [InlineData("thomast", "thomast", true)]
    [InlineData("thomast", "ThomasX", false)]
    [InlineData("thomast", "thomas", false)]
    [InlineData("thomast", null, false)]
    public void IsValidDisplayFor_ShouldAllowOnlyACasingDifference(
        string username,
        string? display,
        bool expected)
    {
        UsernamePolicy.IsValidDisplayFor(username, display).Should().Be(expected);
    }

    /// <summary>
    /// Kept as one rule set: the older single-value method must stay a pure projection of the newer
    /// one, or the two could drift into validating differently.
    /// </summary>
    [Theory]
    [InlineData("ThomasT")]
    [InlineData("  spaced  ")]
    [InlineData("a-b")]
    public void NormalizeAndValidate_ShouldAgreeWithTheDisplayForm(string input)
    {
        UsernamePolicy.NormalizeAndValidate(input)
            .Should()
            .Be(UsernamePolicy.NormalizeAndValidateWithDisplay(input).Username);
    }

    /// <summary>
    /// The message is shown to someone whose capitals we now keep, so it must not claim otherwise.
    /// </summary>
    [Fact]
    public void FormatMessage_ShouldNotClaimUsernamesAreLowercase()
    {
        UsernamePolicy.FormatMessage.Should().NotContain("lowercase");
    }

    /// <summary>
    /// The hole a normalised-only check leaves open. U+212A KELVIN SIGN lowercases to an ASCII 'k',
    /// so this value normalises to a perfectly clean "kelvin" and satisfies every rule that looks
    /// at the normalised form — while the string that would be stored and rendered is a non-ASCII
    /// homoglyph. Worse than cosmetic: CK_Users_UsernameDisplay_Normalizes compares PostgreSQL's
    /// collation-dependent lower() against the key, so where it does not agree with
    /// ToLowerInvariant the row fails the constraint and a signup becomes a 500 rather than a 400.
    /// </summary>
    [Theory]
    [InlineData("Kelvin")]       // KELVIN SIGN + elvin -> "kelvin"
    [InlineData("İstanbul")]     // LATIN CAPITAL I WITH DOT ABOVE
    [InlineData("Admın")]        // DOTLESS I
    [InlineData("café")]         // plainly non-ASCII
    public void NormalizeAndValidateWithDisplay_ShouldRejectANonAsciiDisplay(string username)
    {
        var act = () => UsernamePolicy.NormalizeAndValidateWithDisplay(username);

        act.Should().Throw<BadRequestException>().WithMessage(UsernamePolicy.FormatMessage);
    }

    [Fact]
    public void NormalizeAndValidateWithDisplay_ShouldStillAcceptOrdinaryMixedCase()
    {
        var forms = UsernamePolicy.NormalizeAndValidateWithDisplay("SmartCat23");

        forms.Username.Should().Be("smartcat23");
        forms.Display.Should().Be("SmartCat23");
    }

    /// <summary>
    /// The re-validation hook untrusted write paths depend on has to close the same hole, or a
    /// display arriving from a cached payload or a seeder could still land a homoglyph.
    /// </summary>
    [Fact]
    public void IsValidDisplayFor_ShouldRejectAHomoglyphThatNormalizesCorrectly()
    {
        const string homoglyph = "Kelvin";

        UsernamePolicy.Normalize(homoglyph).Should().Be("kelvin");
        UsernamePolicy.IsValidDisplayFor("kelvin", homoglyph).Should().BeFalse();
        UsernamePolicy.IsValidDisplayFor("kelvin", "Kelvin").Should().BeTrue();
    }
}
