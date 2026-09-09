using backend.main.features.cache;

namespace backend.main.features.events.recentlyviewed
{
    /// <summary>
    /// Cache keys for recently-viewed state.
    /// <para>
    /// Only the opt-out setting is cached. It is consulted on every single record-view call and
    /// changes approximately never, which is the entire win. The list deliberately is not: every
    /// event the user opens would invalidate it, so it would buy a poor hit rate in exchange for
    /// invalidation churn on the hottest write path in the slice.
    /// </para>
    /// </summary>
    public static class RecentlyViewedCacheKeys
    {
        public static string Settings(int userId) => $"recentevt:settings:u:{userId}";

        public static Task InvalidateUserAsync(IRefreshAheadCache cache, int userId) =>
            cache.RemoveAsync(Settings(userId));
    }
}
