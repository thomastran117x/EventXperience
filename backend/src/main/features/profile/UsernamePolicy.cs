using System.Collections.Frozen;

using backend.main.shared.exceptions.http;

namespace backend.main.features.profile;

/// <summary>
/// The normalisation and format rules for account usernames.
/// </summary>
/// <remarks>
/// The split between <see cref="Normalize"/> and <see cref="NormalizeAndValidate"/> is the whole
/// contract: <b>lookup paths call <see cref="Normalize"/> and get no format rules; only write paths
/// validate.</b> Rows that predate this policy do not satisfy it — the
/// <c>20260815023000_backfillusernames</c> migration derived usernames from the email local part
/// keeping <c>a-zA-Z0-9._-</c> without lowercasing, with no minimum length and no guard against
/// leading, trailing, or repeated separators. Applying the format rules to a lookup would lock those
/// accounts out of login and 400 their public profiles, so a reader must never do it.
///
/// The frontend mirrors these rules in
/// <c>frontend/src/app/features/auth/validators/username-format.validator.ts</c>; the two must be
/// changed together.
/// </remarks>
public static class UsernamePolicy
{
    /// <summary>Shortest username a new account may claim.</summary>
    public const int MinLength = 3;

    /// <summary>Longest username a new account may claim, and the width of the stored column.</summary>
    public const int MaxLength = 50;

    /// <summary>
    /// The one message covering the charset and placement rules, kept as a single string so the
    /// frontend mirror does not have to reproduce a decision tree to say the same thing.
    /// </summary>
    public const string FormatMessage =
        "Username may use only lowercase letters, numbers, and . _ -, "
        + "must start and end with a letter or number, and cannot repeat . _ -.";

    /// <summary>
    /// Names withheld from signup because holding one lets an account pass for staff. Not a route
    /// reservation — public profiles live under <c>/profile/{username}</c>, so nothing here collides
    /// with an application path.
    /// </summary>
    public static IReadOnlySet<string> ReservedNames
    {
        get;
    } = FrozenSet.ToFrozenSet(
    [
        "admin",
        "administrator",
        "anonymous",
        "api",
        "moderator",
        "null",
        "official",
        "root",
        "security",
        "staff",
        "superuser",
        "support",
        "system",
        "undefined"
    ], StringComparer.Ordinal);

    /// <summary>
    /// The form to store, hash, and look up by. Applies no format rules — see the type remarks.
    /// </summary>
    public static string Normalize(string? username) =>
        (username ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Whether an already-normalised value satisfies the format rules, without throwing.
    /// </summary>
    public static bool IsWellFormed(string normalized)
    {
        if (normalized.Length < MinLength || normalized.Length > MaxLength)
            return false;

        if (!IsAlphanumeric(normalized[0]) || !IsAlphanumeric(normalized[^1]))
            return false;

        var previousWasSeparator = false;
        foreach (var character in normalized)
        {
            if (IsAlphanumeric(character))
            {
                previousWasSeparator = false;
                continue;
            }

            if (!IsSeparator(character) || previousWasSeparator)
                return false;

            previousWasSeparator = true;
        }

        return true;
    }

    /// <summary>
    /// Normalises and enforces the format rules. Use on write paths only.
    /// </summary>
    public static string NormalizeAndValidate(string? username)
    {
        var normalized = Normalize(username);
        if (normalized.Length == 0)
            throw new BadRequestException("Username is required.");

        if (normalized.Length < MinLength)
            throw new BadRequestException($"Username must be at least {MinLength} characters.");

        if (normalized.Length > MaxLength)
            throw new BadRequestException($"Username must be {MaxLength} characters or fewer.");

        if (!IsWellFormed(normalized))
            throw new BadRequestException(FormatMessage);

        // Deliberately vague: naming the list would tell a caller which handles to go looking for.
        if (ReservedNames.Contains(normalized))
            throw new BadRequestException("That username is not available.");

        return normalized;
    }

    // Restricted to ASCII on purpose. Normalize lowercases with the invariant culture, so a value
    // that survives this is stable across cultures and matches byte-for-byte in the bloom filter.
    private static bool IsAlphanumeric(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsSeparator(char character) => character is '.' or '_' or '-';
}
