namespace backend.main.features.auth;

/// <summary>
/// Answers "does an account already use this email", optionally using the bloom filter to skip the
/// database when it can prove the address is unknown.
/// </summary>
public interface IEmailAvailabilityService
{
    /// <summary>
    /// True when a user row already holds the address.
    /// </summary>
    /// <remarks>
    /// Same contract as <c>IAuthUserRepository.EmailExistsAsync</c>, and deliberately the same
    /// polarity so call sites read identically. A false answer is authoritative for the instant it
    /// is produced; the unique index on the column remains the thing that actually prevents a
    /// duplicate account.
    /// </remarks>
    /// <param name="normalizedEmail">Address already passed through <c>EmailPolicy</c>.</param>
    /// <param name="mode">Whether the filter may answer on its own. Defaults to authoritative.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<bool> IsRegisteredAsync(
        string normalizedEmail,
        AvailabilityLookupMode mode = AvailabilityLookupMode.Authoritative,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True only when the filter proves no account uses the address.
    /// </summary>
    /// <remarks>
    /// For callers that already have their own query to run and only want to skip it. Unlike
    /// <see cref="IsRegisteredAsync"/> this never touches the database — it returns false whenever
    /// the filter is unready, disabled, or merely unsure, so a caller falls through to the lookup
    /// it was going to make anyway and no path pays for an extra round trip.
    ///
    /// Advisory by construction: the local bitmap can lag a signup committed on another instance,
    /// so never gate a write on this.
    /// </remarks>
    /// <param name="normalizedEmail">Address already passed through <c>EmailPolicy</c>.</param>
    bool IsDefinitelyUnregistered(string normalizedEmail);

    /// <summary>
    /// Records that an address now belongs to an account. Call after the insert has committed.
    /// </summary>
    Task MarkRegisteredAsync(string normalizedEmail, CancellationToken cancellationToken = default);
}
