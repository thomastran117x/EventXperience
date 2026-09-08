using System.Security.Cryptography;
using System.Text;

using backend.main.features.profile;
using backend.main.features.profile.suggestions;

using FluentAssertions;

namespace backend.tests.Unit.Features.Profile;

/// <summary>
/// Pins the vocabulary a suggested username is built from.
/// </summary>
/// <remarks>
/// These tests are the only thing standing between a word-list edit and a username the product
/// would be embarrassed by, so they are deliberately exhaustive rather than sampled: the full cross
/// product is only ~250k pairs and runs in well under a second.
/// </remarks>
public class UsernameWordListsTests
{
    public static TheoryData<string> ListNames => new() { "adjectives", "nouns" };

    /// <summary>
    /// Also proves the embedded resources resolve at all. Their LogicalNames are set by hand in
    /// backend.csproj, so a folder rename would otherwise fail silently at runtime.
    /// </summary>
    [Theory]
    [MemberData(nameof(ListNames))]
    public void Lists_ShouldLoadAndMeetTheTargetSize(string name)
    {
        var words = Words(name);

        words.Should().NotBeEmpty();
        words.Length.Should().BeGreaterThanOrEqualTo(UsernameWordLists.MinimumListSize);
    }

    [Theory]
    [MemberData(nameof(ListNames))]
    public void Words_ShouldBeThreeToTenLowercaseAsciiLetters(string name)
    {
        Words(name)
            .Should()
            .OnlyContain(word =>
                word.Length >= 3
                && word.Length <= 10
                && word.All(character => character >= 'a' && character <= 'z'));
    }

    [Theory]
    [MemberData(nameof(ListNames))]
    public void Words_ShouldNotRepeat(string name)
    {
        var words = Words(name);

        words.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// A word in both lists would let a suggestion repeat itself, e.g. <c>OliveOlive23</c>.
    /// </summary>
    [Fact]
    public void Lists_ShouldNotOverlap()
    {
        UsernameWordLists.Adjectives
            .Intersect(UsernameWordLists.Nouns, StringComparer.Ordinal)
            .Should()
            .BeEmpty();
    }

    /// <summary>
    /// Curation, checked. Words are chosen so that no single word carries a denied substring, which
    /// is what lets <see cref="UsernameSuggestionService"/> reject on the composed name without
    /// tripping over a legitimate word that merely contains one — the Scunthorpe problem. Words like
    /// <c>peacock</c> and <c>raccoon</c> were dropped for exactly this reason.
    /// </summary>
    [Theory]
    [MemberData(nameof(ListNames))]
    public void Words_ShouldNotThemselvesContainADeniedSubstring(string name)
    {
        var offenders = Words(name)
            .Where(UsernameWordLists.ContainsDeniedSubstring)
            .ToList();

        offenders.Should().BeEmpty();
    }

    /// <summary>
    /// The guarantee the generator leans on: every possible pairing produces a name the write path
    /// will accept, so a suggestion can never be offered and then rejected on format grounds.
    /// </summary>
    [Fact]
    public void EveryPair_ShouldProduceAWellFormedUnreservedUsername()
    {
        foreach (var adjective in UsernameWordLists.Adjectives)
        {
            foreach (var noun in UsernameWordLists.Nouns)
            {
                // The shortest and longest suffixes the tier ladder can produce.
                foreach (var number in new[] { "10", "9999" })
                {
                    var username = adjective + noun + number;

                    UsernamePolicy.IsWellFormed(username).Should().BeTrue($"'{username}' must be claimable");
                    UsernamePolicy.ReservedNames.Should().NotContain(username);
                }
            }
        }
    }

    /// <summary>
    /// The check curation cannot do. Concatenation creates tokens neither word contains — brisk +
    /// lantern spans "klan", cloudy + kestrel spans "dyke" — so the seam is where the risk lives.
    /// This asserts the rate stays negligible rather than zero: the generator rejects these at draw
    /// time, and a handful of losses out of a quarter-million pairs costs the namespace nothing.
    /// A jump here means a word-list edit widened the seam and wants a look.
    /// </summary>
    [Fact]
    public void SeamCrossingRejections_ShouldStayNegligible()
    {
        var adjectives = UsernameWordLists.Adjectives;
        var nouns = UsernameWordLists.Nouns;
        var rejected = 0;

        foreach (var adjective in adjectives)
        {
            foreach (var noun in nouns)
            {
                if (UsernameWordLists.ContainsDeniedSubstring(adjective + noun))
                    rejected++;
            }
        }

        var total = adjectives.Length * (long)nouns.Length;
        (rejected / (double)total).Should().BeLessThan(0.005);
    }

    /// <summary>
    /// Numbers carry meaning too, and a suffix is not something curation of the words can catch.
    /// Pinned so a change is deliberate.
    /// </summary>
    [Fact]
    public void DeniedNumbers_ShouldBePinnedSoChangesAreDeliberate()
    {
        UsernameWordLists.DeniedNumbers
            .Should()
            .BeEquivalentTo(new[] { 14, 18, 88, 187, 420, 666, 1312, 1488, 1919, 8814 });
    }

    /// <summary>
    /// The highest-value test here. Without it, words slipped into a thousand-line file arrive
    /// unreviewed; with it, any edit turns the build red and forces someone to look at the diff.
    /// Update the hashes in the same commit that changes a list, having actually read the change.
    /// </summary>
    [Theory]
    [InlineData("adjectives", "d8b3480dcd9b08f7e497b0fdd4b03145f26fadf4e239a5ad28153f85dfe30121")]
    [InlineData("nouns", "408553b8ba84d85477c1d1ac7baf8189208209ebed3647487ee0c55b8d5d0e0f")]
    public void WordLists_ShouldBePinnedSoChangesAreDeliberate(string name, string expectedSha256)
    {
        Hash(Words(name)).Should().Be(expectedSha256);
    }

    private static string[] Words(string name) =>
        name == "adjectives"
            ? [.. UsernameWordLists.Adjectives]
            : [.. UsernameWordLists.Nouns];

    private static string Hash(IEnumerable<string> words)
    {
        var joined = string.Join('\n', words.OrderBy(word => word, StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }
}
