namespace backend.main.features.auth;

/// <summary>
/// Answers "is this username already spoken for", optionally using the bloom filter to skip the
/// database when it can prove the name is free.
/// </summary>
public interface IUsernameAvailabilityService
{
    /// <summary>
    /// True when the username is held by a user or covered by an active reservation.
    /// </summary>
    /// <remarks>
    /// Same contract as <c>IAuthUserRepository.UsernameUnavailableAsync</c>, and deliberately the
    /// same polarity so call sites read identically. A false answer is authoritative for the
    /// instant it is produced; it is not a reservation, and the unique index remains the thing
    /// that actually prevents a duplicate.
    /// </remarks>
    /// <param name="normalizedUsername">Username already passed through <c>UsernamePolicy</c>.</param>
    /// <param name="utcNow">Clock reading used to test reservation expiry.</param>
    /// <param name="mode">Whether the filter may answer on its own. Defaults to authoritative.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<bool> IsUnavailableAsync(
        string normalizedUsername,
        DateTime utcNow,
        AvailabilityLookupMode mode = AvailabilityLookupMode.Authoritative,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The subset of <paramref name="normalizedUsernames"/> that is already spoken for.
    /// </summary>
    /// <remarks>
    /// The batched form of <see cref="IsUnavailableAsync"/>, for callers holding a handful of
    /// candidates at once. It exists to bound the cost: probing candidates one at a time issues a
    /// query per candidate whenever the filter cannot clear them, and
    /// <c>DisabledBloomFilterRegistry</c> answers <c>Unavailable</c> for every lookup, so with the
    /// bloom feature flag off that is the normal case rather than the rare one. This performs one
    /// query regardless, and none at all when the filter proves every candidate absent.
    ///
    /// Same advisory contract as the single-value form: a name absent from the result was free when
    /// it was checked, not reserved.
    /// </remarks>
    /// <param name="normalizedUsernames">Usernames already passed through <c>UsernamePolicy</c>.</param>
    /// <param name="utcNow">Clock reading used to test reservation expiry.</param>
    /// <param name="mode">Whether the filter may answer on its own. Defaults to authoritative.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<IReadOnlySet<string>> FindUnavailableAsync(
        IReadOnlyCollection<string> normalizedUsernames,
        DateTime utcNow,
        AvailabilityLookupMode mode = AvailabilityLookupMode.Authoritative,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a username is now taken. Call after the claiming write has committed.
    /// </summary>
    Task MarkTakenAsync(string normalizedUsername, CancellationToken cancellationToken = default);
}
