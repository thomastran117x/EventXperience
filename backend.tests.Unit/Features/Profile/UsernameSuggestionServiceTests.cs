using backend.main.features.auth;
using backend.main.features.profile;
using backend.main.features.profile.suggestions;

using FluentAssertions;

using Moq;

namespace backend.tests.Unit.Features.Profile;

public class UsernameSuggestionServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SuggestAsync_ShouldReturnThreeClaimableNames()
    {
        var service = CreateService(NothingTaken());

        var suggestions = await service.SuggestAsync();

        suggestions.Should().HaveCount(3);
        suggestions.Should().OnlyHaveUniqueItems();
        foreach (var suggestion in suggestions)
        {
            UsernamePolicy.IsWellFormed(suggestion.Username).Should().BeTrue();
            UsernamePolicy.ReservedNames.Should().NotContain(suggestion.Username);
        }
    }

    /// <summary>
    /// The invariant the whole display column rests on. A suggestion whose display did not lowercase
    /// back to its username would be stored as a row whose rendered handle and profile URL disagree.
    /// </summary>
    [Fact]
    public async Task SuggestAsync_ShouldReturnADisplayThatNormalizesToTheUsername()
    {
        var service = CreateService(NothingTaken());

        var suggestions = await service.SuggestAsync();

        suggestions.Should().NotBeEmpty();
        foreach (var suggestion in suggestions)
        {
            UsernamePolicy.Normalize(suggestion.Display).Should().Be(suggestion.Username);
            UsernamePolicy.IsValidDisplayFor(suggestion.Username, suggestion.Display).Should().BeTrue();
            suggestion.Display.Should().MatchRegex("^[A-Z][a-z]+[A-Z][a-z]+[0-9]+$");
        }
    }

    /// <summary>
    /// Three names that share a word read as variants of one another rather than as a choice.
    /// </summary>
    [Fact]
    public async Task SuggestAsync_ShouldNotRepeatAWordAcrossSuggestions()
    {
        var service = CreateService(NothingTaken());

        var suggestions = await service.SuggestAsync();

        var adjectives = suggestions.Select(s => Adjective(s.Username)).ToList();
        var nouns = suggestions.Select(s => Noun(s.Username)).ToList();
        adjectives.Should().OnlyHaveUniqueItems();
        nouns.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The performance contract. Candidates are checked as a batch, so a request costs one query per
    /// tier rather than one per candidate — which is what a naive loop would cost every time the
    /// bloom filter is disabled, since DisabledBloomFilterRegistry answers Unavailable for every
    /// lookup and nothing can then be cleared without asking the database.
    /// </summary>
    [Fact]
    public async Task SuggestAsync_ShouldCheckCandidatesInOneBatch()
    {
        var availability = NothingTaken();
        var service = CreateService(availability);

        await service.SuggestAsync();

        availability.Verify(
            a => a.FindUnavailableAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTime>(),
                AvailabilityLookupMode.Advisory,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SuggestAsync_ShouldSkipNamesThatAreAlreadyTaken()
    {
        // Everything drawn in the first round is taken, so the accepted names must come from a later
        // one, and none of the rejected candidates may appear in the result.
        var firstBatch = new List<string>();
        var availability = new Mock<IUsernameAvailabilityService>();
        availability
            .Setup(a => a.FindUnavailableAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTime>(),
                It.IsAny<AvailabilityLookupMode>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> names, DateTime _, AvailabilityLookupMode _, CancellationToken _) =>
            {
                if (firstBatch.Count == 0)
                {
                    firstBatch.AddRange(names);
                    return new HashSet<string>(names, StringComparer.Ordinal);
                }

                return new HashSet<string>(StringComparer.Ordinal);
            });

        var suggestions = await CreateService(availability).SuggestAsync();

        suggestions.Should().HaveCount(3);
        suggestions.Select(s => s.Username).Should().NotIntersectWith(firstBatch);
    }

    /// <summary>
    /// The tier ladder will realistically never fire in production — tier 1 alone is 22.7M names —
    /// so this test is the only thing that ever exercises it.
    /// </summary>
    [Fact]
    public async Task SuggestAsync_ShouldWidenTheSuffix_WhenEveryTwoDigitNameIsTaken()
    {
        var availability = new Mock<IUsernameAvailabilityService>();
        availability
            .Setup(a => a.FindUnavailableAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTime>(),
                It.IsAny<AvailabilityLookupMode>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> names, DateTime _, AvailabilityLookupMode _, CancellationToken _) =>
                new HashSet<string>(names.Where(name => Suffix(name).Length == 2), StringComparer.Ordinal));

        var suggestions = await CreateService(availability).SuggestAsync();

        suggestions.Should().HaveCount(3);
        suggestions.Should().OnlyContain(s => Suffix(s.Username).Length > 2);
    }

    /// <summary>
    /// A form that works without chips must not be broken by the absence of chips.
    /// </summary>
    [Fact]
    public async Task SuggestAsync_ShouldReturnFewerRatherThanThrow_WhenEveryNameIsTaken()
    {
        var availability = new Mock<IUsernameAvailabilityService>();
        availability
            .Setup(a => a.FindUnavailableAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTime>(),
                It.IsAny<AvailabilityLookupMode>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> names, DateTime _, AvailabilityLookupMode _, CancellationToken _) =>
                new HashSet<string>(names, StringComparer.Ordinal));

        var suggestions = await CreateService(availability).SuggestAsync();

        suggestions.Should().BeEmpty();
    }

    /// <summary>
    /// The suffix carries meaning of its own, and no amount of word curation can catch it.
    /// </summary>
    [Fact]
    public async Task SuggestAsync_ShouldNeverEndInADeniedNumber()
    {
        // Walk the number range so every denied value is actually drawn and has to be rejected.
        var draws = 0;
        var service = CreateService(
            NothingTaken(),
            (min, maxExclusive) =>
            {
                draws++;
                return min == 0 ? draws % maxExclusive : min + (draws % (maxExclusive - min));
            });

        var suggestions = await service.SuggestAsync();

        suggestions.Should()
            .OnlyContain(s => !UsernameWordLists.DeniedNumbers.Contains(int.Parse(Suffix(s.Username))));
    }

    [Fact]
    public async Task SuggestAsync_ShouldHonourCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => CreateService(NothingTaken()).SuggestAsync(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static Mock<IUsernameAvailabilityService> NothingTaken()
    {
        var availability = new Mock<IUsernameAvailabilityService>();
        availability
            .Setup(a => a.FindUnavailableAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTime>(),
                It.IsAny<AvailabilityLookupMode>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.Ordinal));
        return availability;
    }

    private static UsernameSuggestionService CreateService(
        Mock<IUsernameAvailabilityService> availability,
        Func<int, int, int>? nextInt = null)
    {
        var random = new Random(20260908);
        return new UsernameSuggestionService(
            availability.Object,
            new FixedTimeProvider(Now),
            nextInt ?? ((min, maxExclusive) => random.Next(min, maxExclusive)));
    }

    private static string Suffix(string username) =>
        new(username.SkipWhile(character => !char.IsDigit(character)).ToArray());

    private static string Adjective(string username) =>
        UsernameWordLists.Adjectives
            .Where(word => username.StartsWith(word, StringComparison.Ordinal))
            .MaxBy(word => word.Length)!;

    private static string Noun(string username)
    {
        var withoutSuffix = username[..^Suffix(username).Length];
        return withoutSuffix[Adjective(username).Length..];
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
