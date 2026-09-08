using System.Collections.Frozen;

using backend.main.shared.exceptions.http;

namespace backend.main.features.profile;

/// <summary>
/// The two forms of a username: the key everything looks up by, and the form shown to people.
/// </summary>
/// <remarks>
/// The display form may differ from the username only by letter case. Nothing else is a legal
/// difference, and nothing may look an account up by the display form.
/// </remarks>
/// <param name="Username">The normalised, lowercase key. Unique, indexed, hashed into the bloom filter.</param>
/// <param name="Display">The trimmed form as the owner wrote it, for rendering only.</param>
public readonly record struct UsernameForms(string Username, string Display);

/// <summary>
/// The normalisation and format rules for account usernames.
/// </summary>
/// <remarks>
/// Two contracts live here, and both are load-bearing.
///
/// <b>Lookup paths call <see cref="Normalize"/> and get no format rules; only write paths
/// validate.</b> Rows that predate this policy do not satisfy it — the
/// <c>20260815023000_backfillusernames</c> migration derived usernames from the email local part
/// keeping <c>a-zA-Z0-9._-</c> without lowercasing, with no minimum length and no guard against
/// leading, trailing, or repeated separators. Applying the format rules to a lookup would lock those
/// accounts out of login and 400 their public profiles, so a reader must never do it.
///
/// <b><c>User.UsernameDisplay</c> is presentation only.</b> It is never a lookup key, never a route
/// parameter, never a join or comparison, and never a value a client sends back. The invariant that
/// keeps it safe is <c>Normalize(display) == Username</c>, which write paths establish through
/// <see cref="NormalizeAndValidateWithDisplay"/> and re-check with <see cref="IsValidDisplayFor"/>.
///
/// Note that mixed case has always been <i>accepted</i> here — <see cref="NormalizeAndValidate"/>
/// lowercases before it validates, so <c>ThomasT</c> passed long before a display column existed.
/// The display form captures what was previously discarded; it relaxes no rule.
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
    /// <remarks>
    /// This deliberately does not say "lowercase". The stored key is lowercased, but the case the
    /// owner typed is preserved in <c>User.UsernameDisplay</c> and shown back to them, so telling
    /// them their capitals are disallowed would be untrue.
    /// </remarks>
    public const string FormatMessage =
        "Username may use only letters, numbers, and . _ -, "
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
    /// Whether a display form is a legal presentation of an already-normalised username — that is,
    /// whether it differs from it by letter case alone.
    /// </summary>
    /// <remarks>
    /// A write path that takes a display form from anywhere other than
    /// <see cref="NormalizeAndValidateWithDisplay"/> — a cached signup payload, a seeder, a caller
    /// that set the property by hand — must re-check it here rather than trust it.
    /// </remarks>
    public static bool IsValidDisplayFor(string normalizedUsername, string? display) =>
        display is not null && Normalize(display) == normalizedUsername;

    /// <summary>
    /// Normalises and enforces the format rules, returning both the lookup key and the display
    /// form. Use on write paths only.
    /// </summary>
    public static UsernameForms NormalizeAndValidateWithDisplay(string? username)
    {
        // Trimmed but not lowercased, so the two forms differ only by case and the
        // Normalize(Display) == Username invariant holds by construction.
        var display = (username ?? string.Empty).Trim();
        var normalized = display.ToLowerInvariant();

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

        return new UsernameForms(normalized, display);
    }

    /// <summary>
    /// Normalises and enforces the format rules. Use on write paths only.
    /// </summary>
    /// <remarks>
    /// Delegates so there is exactly one copy of the rules. A caller that stores the result should
    /// prefer <see cref="NormalizeAndValidateWithDisplay"/> and keep the display form too.
    /// </remarks>
    public static string NormalizeAndValidate(string? username) =>
        NormalizeAndValidateWithDisplay(username).Username;

    // Restricted to ASCII on purpose. Normalize lowercases with the invariant culture, so a value
    // that survives this is stable across cultures and matches byte-for-byte in the bloom filter.
    private static bool IsAlphanumeric(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsSeparator(char character) => character is '.' or '_' or '-';
}
