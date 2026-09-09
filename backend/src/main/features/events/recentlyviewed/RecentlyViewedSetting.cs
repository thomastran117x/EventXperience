namespace backend.main.features.events.recentlyviewed
{
    /// <summary>
    /// A user's opt-out from view tracking.
    /// <para>
    /// An absent row means enabled, so rows exist only for users who have actually touched the
    /// toggle. That keeps this table near-empty and means the feature costs nothing for the
    /// users who never think about it.
    /// </para>
    /// <para>
    /// This lives in the slice rather than on <c>User</c> or in a shared preferences table
    /// because it is meaningless outside this feature: it is read only by
    /// <see cref="RecentlyViewedService"/>, and it should disappear with the feature flag rather
    /// than outlive it as a stray column on the identity aggregate.
    /// </para>
    /// </summary>
    public class RecentlyViewedSetting
    {
        public int Id
        {
            get; set;
        }
        public int UserId
        {
            get; set;
        }
        public bool Enabled { get; set; } = true;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
