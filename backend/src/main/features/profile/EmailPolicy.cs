using backend.main.shared.exceptions.http;

namespace backend.main.features.profile;

/// <summary>
/// The single normalisation rule for account email addresses.
/// </summary>
/// <remarks>
/// The <c>Users.Email</c> column is <c>citext</c>, so the database compares addresses
/// case-insensitively no matter what a caller passes. The bloom filter does not: it hashes the
/// literal string, so a value added as <c>Ada@Example.com</c> sets different bits than the same
/// address probed as <c>ada@example.com</c>, and the filter would then report a registered address
/// as definitely absent. Every path that reads or writes the email filter must therefore normalise
/// through here, exactly as the username filter does through <see cref="UsernamePolicy"/>.
/// </remarks>
public static class EmailPolicy
{
    /// <summary>
    /// Longest address the RFC 5321 forward-path permits, and the width of the stored column.
    /// </summary>
    public const int MaxLength = 254;

    /// <summary>
    /// The form to persist and deliver mail to: surrounding whitespace removed, casing intact.
    /// </summary>
    /// <remarks>
    /// RFC 5321 section 2.4 leaves the local part case-sensitive and reserves its interpretation
    /// to the destination host, so lowercasing an address before storing or mailing it can produce
    /// one that does not exist. Nothing here needs that: the unique index is <c>citext</c>, and the
    /// filter gets its lowercase form from <see cref="Normalize"/> at the point of hashing.
    /// </remarks>
    public static string Sanitize(string? email) => (email ?? string.Empty).Trim();

    /// <summary>
    /// The form to hash and look up by. Never persist this — see <see cref="Sanitize"/>.
    /// </summary>
    public static string Normalize(string? email) => Sanitize(email).ToLowerInvariant();

    /// <summary>
    /// Normalises and applies the cheap structural checks a probe endpoint needs before it is
    /// worth touching the filter or the database.
    /// </summary>
    /// <remarks>
    /// Deliberately not a full RFC 5322 parse. Addresses are proven by delivering to them, not by
    /// pattern-matching them; this only rejects input that could never be an address, so an
    /// obviously malformed value costs a 400 instead of a lookup.
    /// </remarks>
    public static string NormalizeAndValidate(string? email)
    {
        var normalized = Normalize(email);
        if (normalized.Length == 0)
            throw new BadRequestException("Email is required.");

        if (normalized.Length > MaxLength)
            throw new BadRequestException($"Email must be {MaxLength} characters or fewer.");

        // An address needs a local part and a domain part, and exactly one separator between them.
        var separator = normalized.IndexOf('@');
        if (separator <= 0
            || separator == normalized.Length - 1
            || normalized.IndexOf('@', separator + 1) >= 0)
        {
            throw new BadRequestException("Email must be a valid email address.");
        }

        return normalized;
    }
}
