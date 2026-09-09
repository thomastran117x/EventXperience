using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;

namespace backend.main.features.profile.suggestions;

/// <summary>
/// The vocabulary a suggested username is assembled from, plus the safety lists that keep the
/// assembly from producing something offensive.
/// </summary>
/// <remarks>
/// The word files are embedded resources with stable <c>LogicalName</c>s set in
/// <c>backend.csproj</c>, so renaming a folder cannot silently break the lookup.
///
/// Everything here is validated once, on first touch, and a violation throws. A malformed word list
/// is a deployment error — it narrows the namespace or lets an illegal name through — and it should
/// fail loudly at startup rather than surface as an odd suggestion months later.
///
/// <b>Why two layers of safety.</b> Per-word curation cannot catch the real risk, which is that
/// concatenating two innocent words creates a third token at the seam: <c>brisk</c> + <c>lantern</c>
/// is <c>brisklantern</c>, and <c>cloudy</c> + <c>kestrel</c> is <c>cloudykestrel</c>. So the words
/// are curated to contain no denied substring at all (a test pins that), and
/// <see cref="ContainsDeniedSubstring"/> re-checks the composed name at generation time. About
/// 0.05% of pairs are rejected that way, which costs the namespace nothing.
/// </remarks>
public static class UsernameWordLists
{
    /// <summary>Shortest and longest word either list may hold. Bounds the composed length.</summary>
    private const int MinWordLength = 3;
    private const int MaxWordLength = 10;

    /// <summary>
    /// The floor each list must meet. Pinned so a truncated or missing resource fails the build's
    /// tests rather than quietly shrinking the namespace by an order of magnitude.
    /// </summary>
    public const int MinimumListSize = 500;

    private static readonly Lazy<ImmutableArray<string>> LazyAdjectives =
        new(() => LoadWords("adjectives"));

    private static readonly Lazy<ImmutableArray<string>> LazyNouns =
        new(() => LoadWords("nouns"));

    private static readonly Lazy<ImmutableArray<string>> LazyDeniedSubstrings =
        new(() => LoadLines("denied-substrings"));

    /// <summary>The adjective half of a suggestion, lowercase.</summary>
    public static ImmutableArray<string> Adjectives => LazyAdjectives.Value;

    /// <summary>The noun half of a suggestion, lowercase.</summary>
    public static ImmutableArray<string> Nouns => LazyNouns.Value;

    /// <summary>
    /// Substrings a composed username must not contain. Never served to a client and never mirrored
    /// into the frontend bundle — it is a list of exactly the strings we do not want to publish.
    /// </summary>
    public static ImmutableArray<string> DeniedSubstrings => LazyDeniedSubstrings.Value;

    /// <summary>
    /// Numeric suffixes withheld because the number itself carries meaning — extremist codes and
    /// the like. Small enough to be one hash lookup per candidate.
    /// </summary>
    public static FrozenSet<int> DeniedNumbers
    {
        get;
    } = FrozenSet.ToFrozenSet([14, 18, 88, 187, 420, 666, 1312, 1488, 1919, 8814]);

    /// <summary>
    /// Whether a composed, lowercase candidate contains a denied substring. Catches the tokens that
    /// only exist across the adjective/noun seam — see the type remarks.
    /// </summary>
    public static bool ContainsDeniedSubstring(string normalizedCandidate)
    {
        foreach (var denied in DeniedSubstrings)
        {
            if (normalizedCandidate.Contains(denied, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static ImmutableArray<string> LoadWords(string name)
    {
        var words = LoadLines(name);

        if (words.Length < MinimumListSize)
        {
            throw new InvalidOperationException(
                $"Username word list '{name}' holds {words.Length} words, below the {MinimumListSize} minimum.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            if (word.Length is < MinWordLength or > MaxWordLength || !IsLowercaseAscii(word))
            {
                throw new InvalidOperationException(
                    $"Username word list '{name}' holds '{word}', which is not {MinWordLength}-{MaxWordLength} lowercase ASCII letters.");
            }

            if (!seen.Add(word))
                throw new InvalidOperationException($"Username word list '{name}' repeats '{word}'.");
        }

        return words;
    }

    private static ImmutableArray<string> LoadLines(string name)
    {
        var resource = $"UsernameWordLists.{name}.txt";
        using var stream = typeof(UsernameWordLists).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resource}' is missing. Check the EmbeddedResource LogicalName entries in backend.csproj.");

        using var reader = new StreamReader(stream);
        var builder = ImmutableArray.CreateBuilder<string>();

        while (reader.ReadLine() is { } line)
        {
            var word = line.Trim();
            if (word.Length > 0)
                builder.Add(word);
        }

        return builder.ToImmutable();
    }

    private static bool IsLowercaseAscii(string word)
    {
        foreach (var character in word)
        {
            if (character is < 'a' or > 'z')
                return false;
        }

        return true;
    }
}
