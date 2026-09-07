using backend.main.features.bloom;
using backend.main.shared.utilities.logger;

namespace backend.main.features.auth;

/// <inheritdoc cref="IUsernameAvailabilityService"/>
public sealed class UsernameAvailabilityService : IUsernameAvailabilityService
{
    private readonly IAuthUserRepository _repository;
    private readonly IBloomFilterRegistry _bloomFilters;

    public UsernameAvailabilityService(
        IAuthUserRepository repository,
        IBloomFilterRegistry bloomFilters)
    {
        _repository = repository;
        _bloomFilters = bloomFilters;
    }

    public async Task<bool> IsUnavailableAsync(
        string normalizedUsername,
        DateTime utcNow,
        AvailabilityLookupMode mode = AvailabilityLookupMode.Authoritative,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // DefinitelyAbsent is the only answer that permits skipping the query. It is exact with
        // respect to this filter — a bloom filter has no false negatives, so a clear bit proves
        // the value was never added to *this* bitmap — but the bitmap itself can lag a claim made
        // on another instance until the next refresh. Callers about to claim the name therefore
        // ask authoritatively, so a stale answer cannot turn a clean conflict into a 500 from the
        // unique index. PossiblyPresent and Unavailable always fall through.
        if (mode == AvailabilityLookupMode.Advisory
            && _bloomFilters.MightContain(BloomFilterTargets.Username, normalizedUsername)
                == BloomFilterLookup.DefinitelyAbsent)
        {
            return false;
        }

        return await _repository.UsernameUnavailableAsync(normalizedUsername, utcNow);
    }

    public async Task MarkTakenAsync(
        string normalizedUsername,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(normalizedUsername))
            return;

        try
        {
            await _bloomFilters.AddAsync(BloomFilterTargets.Username, normalizedUsername, cancellationToken);
        }
        catch (Exception exception)
        {
            // Callers invoke this after the claiming write has already committed, and AuthService
            // converts any non-AppException into a 500 — so letting this throw would report a
            // successful signup as a server error. A missed bit only costs accuracy until the
            // next rebuild, which is strictly the lesser failure.
            Logger.Warn(
                exception,
                $"[UsernameAvailabilityService] Failed to record '{normalizedUsername}' in the username filter.");
        }
    }
}
