namespace backend.main.features.events.recentlyviewed.contracts.responses
{
    /// <summary>The user's view-tracking preference. Enabled unless they have turned it off.</summary>
    public class RecentlyViewedSettingsResponse
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Null while the user has never changed the default.</summary>
        public DateTime? UpdatedAtUtc
        {
            get; set;
        }
    }
}
