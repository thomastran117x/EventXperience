namespace backend.main.features.events.recentlyviewed
{
    /// <summary>
    /// Read-only history queries. Writes go through <see cref="RecentlyViewedService"/> on
    /// <c>AppDatabaseContext</c> directly, matching the favourites and waitlist slices.
    /// </summary>
    public interface IRecentlyViewedRepository
    {
        /// <summary>
        /// The user's history, most recent first, already filtered to entries newer than
        /// <paramref name="cutoff"/> and capped at <paramref name="limit"/>.
        /// <para>
        /// The cutoff is applied here rather than in the service on purpose: the expiry sweep
        /// runs periodically, so between an entry ageing out and the next sweep this filter is
        /// the only thing honouring the retention promise. Putting it in the one place every
        /// read goes through means no caller can forget it.
        /// </para>
        /// </summary>
        Task<IReadOnlyList<RecentlyViewedEvent>> GetRecentAsync(int userId, DateTime cutoff, int limit);

        Task<RecentlyViewedSetting?> GetSettingAsync(int userId);
    }
}
