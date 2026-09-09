namespace backend.main.application.features;

public sealed class FeatureFlagRegistry
{
    private readonly HashSet<string> _keys;

    public static FeatureFlagRegistry Instance
    {
        get;
    } = new(
        [
            FeatureFlagKeys.Auth,
            FeatureFlagKeys.Bloom,
            FeatureFlagKeys.Clubs,
            FeatureFlagKeys.ClubsDiscussions,
            FeatureFlagKeys.ClubsFollow,
            FeatureFlagKeys.ClubsPosts,
            FeatureFlagKeys.ClubsReviews,
            FeatureFlagKeys.ClubsVersioning,
            FeatureFlagKeys.Events,
            FeatureFlagKeys.EventsAnalytics,
            FeatureFlagKeys.EventsFavourites,
            FeatureFlagKeys.EventsImages,
            FeatureFlagKeys.EventsInvitations,
            FeatureFlagKeys.EventsRecentlyViewed,
            FeatureFlagKeys.EventsRecurrence,
            FeatureFlagKeys.EventsRegistration,
            FeatureFlagKeys.EventsVersioning,
            FeatureFlagKeys.EventsWaitlist,
            FeatureFlagKeys.Payment,
            FeatureFlagKeys.Profile,
            FeatureFlagKeys.ProfileAdmin,
            FeatureFlagKeys.Search,
            FeatureFlagKeys.SearchReindex,
            FeatureFlagKeys.Storage,
            FeatureFlagKeys.StorageOrphanCleanup
        ]);

    public FeatureFlagRegistry(IEnumerable<string> keys)
    {
        _keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            if (!_keys.Add(Normalize(key)))
                throw new InvalidOperationException($"Duplicate feature flag key '{key}'.");
        }
    }

    public IReadOnlyCollection<string> Keys => _keys;

    public string Normalize(string featureKey)
    {
        if (string.IsNullOrWhiteSpace(featureKey))
            throw new ArgumentException("Feature flag key cannot be empty.", nameof(featureKey));

        return featureKey.Trim().ToLowerInvariant();
    }

    public bool Contains(string featureKey) => _keys.Contains(Normalize(featureKey));

    public void EnsureKnown(string featureKey)
    {
        var normalized = Normalize(featureKey);
        if (!_keys.Contains(normalized))
            throw new InvalidOperationException($"Unknown feature flag key '{featureKey}'.");
    }

    public IEnumerable<string> GetLineage(string featureKey)
    {
        var normalized = Normalize(featureKey);
        EnsureKnown(normalized);

        var segments = normalized.Split('.');
        for (var length = 1; length <= segments.Length; length++)
            yield return string.Join('.', segments.Take(length));
    }

    public string ToEnvironmentVariableName(string featureKey)
    {
        var normalized = Normalize(featureKey);
        EnsureKnown(normalized);
        return $"FEATURE_{normalized.Replace('.', '_').ToUpperInvariant()}";
    }
}
