using backend.main.features.events.contracts.responses;

namespace backend.main.features.events.recentlyviewed.contracts.responses
{
    /// <summary>One entry in the user's history, carrying the full event so the client can render it directly.</summary>
    public class RecentlyViewedEventResponse
    {
        public int EventId
        {
            get; set;
        }
        public DateTime ViewedAtUtc
        {
            get; set;
        }
        public EventResponse Event { get; set; } = null!;
    }
}
