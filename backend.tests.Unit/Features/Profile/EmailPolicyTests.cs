using backend.main.features.profile;
using backend.main.shared.exceptions.http;

using FluentAssertions;

namespace backend.tests.Unit.Features.Profile;

public class EmailPolicyTests
{
    [Theory]
    [InlineData("  ada@example.com  ", "ada@example.com")]
    [InlineData("Ada@Example.COM", "ada@example.com")]
    [InlineData("ada@example.com", "ada@example.com")]
    public void Normalize_ShouldTrimAndLowercase(string input, string expected)
    {
        EmailPolicy.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ShouldReturnEmpty_ForAMissingAddress(string? input)
    {
        EmailPolicy.Normalize(input).Should().BeEmpty();
    }

    /// <summary>
    /// The property the filter depends on: the source, the probe, and the signup path all hash the
    /// literal string, so normalising twice must not move the value again.
    /// </summary>
    [Theory]
    [InlineData("  Ada@Example.COM  ")]
    [InlineData("grace@example.com")]
    public void Normalize_ShouldBeIdempotent(string input)
    {
        var once = EmailPolicy.Normalize(input);

        EmailPolicy.Normalize(once).Should().Be(once);
    }

    /// <summary>
    /// Sanitize is the form that gets persisted and mailed, so it must not touch casing: RFC 5321
    /// leaves the local part case-sensitive to the destination host.
    /// </summary>
    [Theory]
    [InlineData("  Ada@Example.COM  ", "Ada@Example.COM")]
    [InlineData("ada@example.com", "ada@example.com")]
    [InlineData(null, "")]
    public void Sanitize_ShouldTrimWithoutChangingCase(string? input, string expected)
    {
        EmailPolicy.Sanitize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_ShouldBeSanitizeLowercased()
    {
        const string input = "  Ada@Example.COM  ";

        EmailPolicy.Normalize(input).Should().Be(EmailPolicy.Sanitize(input).ToLowerInvariant());
    }

    [Fact]
    public void NormalizeAndValidate_ShouldReturnTheNormalisedAddress()
    {
        EmailPolicy.NormalizeAndValidate("  Ada@Example.COM  ").Should().Be("ada@example.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeAndValidate_ShouldRejectAMissingAddress(string? input)
    {
        var act = () => EmailPolicy.NormalizeAndValidate(input);

        act.Should().Throw<BadRequestException>().WithMessage("Email is required.");
    }

    [Fact]
    public void NormalizeAndValidate_ShouldRejectAnOverLengthAddress()
    {
        var tooLong = new string('a', EmailPolicy.MaxLength) + "@example.com";

        var act = () => EmailPolicy.NormalizeAndValidate(tooLong);

        act.Should().Throw<BadRequestException>()
            .WithMessage($"Email must be {EmailPolicy.MaxLength} characters or fewer.");
    }

    [Fact]
    public void NormalizeAndValidate_ShouldAcceptAnAddressAtTheLengthLimit()
    {
        var atLimit = new string('a', EmailPolicy.MaxLength - "@example.com".Length) + "@example.com";
        atLimit.Should().HaveLength(EmailPolicy.MaxLength);

        EmailPolicy.NormalizeAndValidate(atLimit).Should().Be(atLimit);
    }

    /// <summary>
    /// Deliberately not a full RFC 5322 parse — only input that could never be an address, so a
    /// malformed value costs a 400 instead of a filter probe and a query.
    /// </summary>
    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("ada@")]
    [InlineData("ada@@example.com")]
    [InlineData("ada@example@com")]
    public void NormalizeAndValidate_ShouldRejectAStructurallyImpossibleAddress(string input)
    {
        var act = () => EmailPolicy.NormalizeAndValidate(input);

        act.Should().Throw<BadRequestException>().WithMessage("Email must be a valid email address.");
    }

    [Theory]
    [InlineData("ada@example.com")]
    [InlineData("ada.lovelace+tag@sub.example.co.uk")]
    public void NormalizeAndValidate_ShouldAcceptOrdinaryAddresses(string input)
    {
        EmailPolicy.NormalizeAndValidate(input).Should().Be(input);
    }
}
