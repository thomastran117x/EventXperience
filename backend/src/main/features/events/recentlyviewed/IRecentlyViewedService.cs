using backend.main.features.events.recentlyviewed.contracts.requests;
using backend.main.features.events.recentlyviewed.contracts.responses;

namespace backend.main.features.events.recentlyviewed
{
    public interface IRecentlyViewedService
    {
        /// <summary>
        /// Records that the user looked at an event, moving it to the head of their history.
        /// Idempotent — a repeat view bumps the timestamp rather than adding a row, so the
        /// client is free to call this on every page load.
        /// <para>
        /// Returns <c>Recorded = false</c> rather than throwing when the user has opted out.
        /// </para>
        /// </summary>
        Task<RecordEventViewResponse> RecordViewAsync(int eventId, int userId, string userRole);

        /// <summary>
        /// The user's history, most recent first, with events they can no longer see dropped.
        /// Empty while the user has tracking switched off, even though their rows are retained.
        /// </summary>
        Task<IReadOnlyList<RecentlyViewedEventResponse>> GetMyRecentlyViewedAsync(int userId, string userRole);

        /// <summary>Removes one entry. Idempotent, and returns whether a row was actually removed.</summary>
        Task<bool> RemoveAsync(int eventId, int userId);

        /// <summary>Removes a selected subset in one statement. Returns how many rows went.</summary>
        Task<int> RemoveManyAsync(IEnumerable<int> eventIds, int userId);

        /// <summary>Wipes the whole history. Returns how many rows went.</summary>
        Task<int> ClearAsync(int userId);

        /// <summary>
        /// Folds a browser-held history into the account, keeping the later timestamp wherever
        /// both sides know an event. Silently skips ids the user cannot see.
        /// </summary>
        Task<RecentlyViewedMergeResultResponse> MergeAsync(MergeRecentlyViewedRequest request, int userId, string userRole);

        Task<RecentlyViewedSettingsResponse> GetSettingsAsync(int userId);

        /// <summary>
        /// Switches tracking on or off. Switching off stops collection but retains what is
        /// already stored — deleting is <see cref="ClearAsync"/>, a separate deliberate act.
        /// </summary>
        Task<RecentlyViewedSettingsResponse> UpdateSettingsAsync(bool enabled, int userId);
    }
}
