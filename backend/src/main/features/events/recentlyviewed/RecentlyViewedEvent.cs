namespace backend.main.features.events.recentlyviewed
{
    /// <summary>
    /// One event a user has looked at, held so they can find their way back to it.
    /// <para>
    /// Deliberately carries no view counter and no first-viewed timestamp. Neither is needed to
    /// present a most-recent-first list, and both would turn the repeat-view path — by far the
    /// most common one — from a single set-based UPDATE into a read-modify-write.
    /// </para>
    /// </summary>
    public class RecentlyViewedEvent
    {
        public int Id
        {
            get; set;
        }
        public int UserId
        {
            get; set;
        }
        public int EventId
        {
            get; set;
        }

        /// <summary>
        /// The most recent view, not the first. A repeat view bumps this, which is what moves
        /// the event back to the head of the list.
        /// </summary>
        public DateTime ViewedAt { get; set; } = DateTime.UtcNow;
    }
}
