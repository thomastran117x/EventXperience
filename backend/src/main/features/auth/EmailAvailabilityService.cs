using backend.main.features.bloom;
using backend.main.shared.utilities.logger;

namespace backend.main.features.auth;

/// <inheritdoc cref="IEmailAvailabilityService"/>
public sealed class EmailAvailabilityService : IEmailAvailabilityService
{
    private readonly IAuthUserRepository _repository;
    private readonly IBloomFilterRegistry _bloomFilters;

    public EmailAvailabilityService(
        IAuthUserRepository repository,
        IBloomFilterRegistry bloomFilters)
    {
        _repository = repository;
        _bloomFilters = bloomFilters;
    }

    public async Task<bool> IsRegisteredAsync(
        string normalizedEmail,
        AvailabilityLookupMode mode = AvailabilityLookupMode.Authoritative,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // DefinitelyAbsent is the only answer that permits skipping the query, and only for a
        // caller that is not about to write. A bloom filter has no false negatives, so a clear bit
        // proves the address was never added to *this* bitmap — but the bitmap can lag a signup
        // committed on another instance until the next refresh. Every path that creates or
        // authenticates an account therefore asks authoritatively.
        if (mode == AvailabilityLookupMode.Advisory
            && _bloomFilters.MightContain(BloomFilterTargets.Email, normalizedEmail)
                == BloomFilterLookup.DefinitelyAbsent)
        {
            return false;
        }

        return await _repository.EmailExistsAsync(normalizedEmail);
    }

    public bool IsDefinitelyUnregistered(string normalizedEmail) =>
        !string.IsNullOrEmpty(normalizedEmail)
        && _bloomFilters.MightContain(BloomFilterTargets.Email, normalizedEmail)
            == BloomFilterLookup.DefinitelyAbsent;

    public async Task MarkRegisteredAsync(
        string normalizedEmail,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(normalizedEmail))
            return;

        try
        {
            await _bloomFilters.AddAsync(BloomFilterTargets.Email, normalizedEmail, cancellationToken);
        }
        catch (Exception exception)
        {
            // Callers invoke this after the account row has already committed, and AuthService
            // converts any non-AppException into a 500 — so letting this throw would report a
            // successful signup as a server error. A missed bit only costs accuracy until the
            // next rebuild, which is strictly the lesser failure.
            Logger.Warn(
                exception,
                $"[EmailAvailabilityService] Failed to record '{normalizedEmail}' in the email filter.");
        }
    }
}
