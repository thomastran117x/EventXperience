using backend.main.features.auth;

namespace backend.main.features.profile.suggestions;

/// <inheritdoc cref="IUsernameSuggestionService"/>
public sealed class UsernameSuggestionService : IUsernameSuggestionService
{
    /// <summary>How many names a caller is handed. Fixed, because all three forms want three.</summary>
    public const int SuggestionCount = 3;

    /// <summary>
    /// The widths the numeric suffix escalates through. Tier 1 alone is 501 x 503 x 90 = 22.7M
    /// names, so at any realistic account count the first draw succeeds and the later tiers never
    /// run in production — they exist so the generator degrades instead of looping. Their only
    /// exercise is the unit tests, which drive them through the injected RNG.
    /// </summary>
    private static readonly (int Min, int MaxExclusive)[] NumberTiers =
        [(10, 100), (100, 1000), (1000, 10000)];

    /// <summary>
    /// Candidates drawn per round trip. Oversampled against <see cref="SuggestionCount"/> so a round
    /// that loses some to the denied lists or to a collision still fills the list in one query.
    /// </summary>
    private const int CandidatesPerDraw = SuggestionCount * 4;

    /// <summary>Rounds attempted per tier before widening the suffix.</summary>
    private const int DrawsPerTier = 2;

    private readonly IUsernameAvailabilityService _availability;
    private readonly TimeProvider _clock;
    private readonly Func<int, int, int> _nextInt;

    public UsernameSuggestionService(IUsernameAvailabilityService availability, TimeProvider clock)
        // Random.Shared, not RandomNumberGenerator, against the convention everywhere else in this
        // codebase — so the choice does not read as an oversight: these are display-only strings the
        // user can accept or overwrite. They are not tokens, not secrets, and not reservations, and
        // no security property rests on their unpredictability. Someone who predicts SmartCat23
        // learns nothing they could not learn from GET /auth/username/availability, and a predicted
        // suggestion buys them nothing beyond making a stranger see a different name. Random.Shared
        // is thread-safe and allocation-free; a CSPRNG draw per number would cost more for nothing.
        : this(availability, clock, Random.Shared.Next)
    {
    }

    /// <summary>Test seam: lets a unit test drive the tier ladder deterministically.</summary>
    internal UsernameSuggestionService(
        IUsernameAvailabilityService availability,
        TimeProvider clock,
        Func<int, int, int> nextInt)
    {
        _availability = availability;
        _clock = clock;
        _nextInt = nextInt;
    }

    public async Task<IReadOnlyList<UsernameSuggestion>> SuggestAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var accepted = new List<UsernameSuggestion>(SuggestionCount);
        var usedAdjectives = new HashSet<string>(StringComparer.Ordinal);
        var usedNouns = new HashSet<string>(StringComparer.Ordinal);
        var utcNow = _clock.GetUtcNow().UtcDateTime;

        for (var tier = 0; tier < NumberTiers.Length && accepted.Count < SuggestionCount; tier++)
        {
            for (var draw = 0; draw < DrawsPerTier && accepted.Count < SuggestionCount; draw++)
            {
                // The last round of the last tier drops the "no repeated adjective or noun" rule, so
                // wanting variety can never itself be the reason we hand back fewer than three.
                var requireDistinctWords =
                    tier != NumberTiers.Length - 1 || draw != DrawsPerTier - 1;

                var candidates = Draw(
                    NumberTiers[tier], usedAdjectives, usedNouns, requireDistinctWords);
                if (candidates.Count == 0)
                    continue;

                // One round trip for the whole batch. Advisory, so a hydrated bloom filter answers
                // for the names it can prove absent and only the rest reach the database — and with
                // the filter disabled this is still a single query, not one per candidate.
                var taken = await _availability.FindUnavailableAsync(
                    candidates.Select(candidate => candidate.Username).ToList(),
                    utcNow,
                    AvailabilityLookupMode.Advisory,
                    cancellationToken);

                foreach (var candidate in candidates)
                {
                    if (accepted.Count == SuggestionCount)
                        break;

                    if (taken.Contains(candidate.Username))
                        continue;

                    if (requireDistinctWords
                        && (usedAdjectives.Contains(candidate.Adjective)
                            || usedNouns.Contains(candidate.Noun)))
                    {
                        continue;
                    }

                    usedAdjectives.Add(candidate.Adjective);
                    usedNouns.Add(candidate.Noun);
                    accepted.Add(new UsernameSuggestion(candidate.Username, candidate.Display));
                }
            }
        }

        return accepted;
    }

    private List<Candidate> Draw(
        (int Min, int MaxExclusive) tier,
        IReadOnlySet<string> usedAdjectives,
        IReadOnlySet<string> usedNouns,
        bool requireDistinctWords)
    {
        var adjectives = UsernameWordLists.Adjectives;
        var nouns = UsernameWordLists.Nouns;

        var candidates = new List<Candidate>(CandidatesPerDraw);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var attempt = 0; attempt < CandidatesPerDraw; attempt++)
        {
            var adjective = adjectives[_nextInt(0, adjectives.Length)];
            var noun = nouns[_nextInt(0, nouns.Length)];

            if (requireDistinctWords
                && (usedAdjectives.Contains(adjective) || usedNouns.Contains(noun)))
            {
                continue;
            }

            var number = _nextInt(tier.Min, tier.MaxExclusive);
            if (UsernameWordLists.DeniedNumbers.Contains(number))
                continue;

            var display = Capitalize(adjective) + Capitalize(noun) + number.ToString();
            var username = display.ToLowerInvariant();

            // Catches the tokens that exist only across the adjective/noun seam — brisk + lantern,
            // cloudy + kestrel. Neither word carries them, so only the composed name can be checked.
            if (UsernameWordLists.ContainsDeniedSubstring(username))
                continue;

            // Unreachable given the word-list rules — every word is 3-10 lowercase letters, so the
            // name is 8-24 alphanumeric characters that end in a digit, while every reserved name is
            // digit-free. Kept because it costs two lines and pins the invariant against a future
            // edit to the word files.
            if (!UsernamePolicy.IsWellFormed(username) || UsernamePolicy.ReservedNames.Contains(username))
                continue;

            if (seen.Add(username))
                candidates.Add(new Candidate(username, display, adjective, noun));
        }

        return candidates;
    }

    private static string Capitalize(string word) =>
        string.Concat(char.ToUpperInvariant(word[0]).ToString(), word.AsSpan(1));

    private readonly record struct Candidate(
        string Username,
        string Display,
        string Adjective,
        string Noun);
}
