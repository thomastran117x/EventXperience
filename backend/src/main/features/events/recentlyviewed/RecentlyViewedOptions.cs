namespace backend.main.features.events.recentlyviewed;

public sealed class RecentlyViewedOptions
{
    /// <summary>How many events one user's history holds. Enforced on write and again on read.</summary>
    public int MaxItemsPerUser { get; set; } = 50;

    /// <summary>
    /// How long an entry survives. Applied as a read filter as well as by the background sweep,
    /// because between an entry crossing the boundary and the next sweep the filter is the only
    /// thing making the promise true.
    /// </summary>
    public int RetentionDays { get; set; } = 90;

    public int PurgeBatchSize { get; set; } = 500;
    public bool PurgeEnabled { get; set; } = true;

    /// <summary>
    /// Bounds one sweep, so a single scoped DbContext is not held open for an unbounded loop
    /// on a backlog. Whatever is left is picked up on the next pass.
    /// </summary>
    public int MaxPurgeBatchesPerRun { get; set; } = 20;
}
