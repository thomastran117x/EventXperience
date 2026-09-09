namespace backend.main.features.events.recentlyviewed.contracts.responses
{
    /// <summary>
    /// What a sync of the browser-held history actually did.
    /// <para>
    /// <see cref="Skipped"/> lumps together every reason an item did not land — expired, or an
    /// event the caller cannot see. Reporting per-item reasons would turn this endpoint into an
    /// oracle for probing which private events exist.
    /// </para>
    /// </summary>
    public class RecentlyViewedMergeResultResponse
    {
        public int Merged
        {
            get; set;
        }
        public int Skipped
        {
            get; set;
        }
        public int Total
        {
            get; set;
        }
    }
}
