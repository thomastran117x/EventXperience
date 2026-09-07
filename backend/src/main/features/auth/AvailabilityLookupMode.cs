namespace backend.main.features.auth;

/// <summary>
/// How much a caller is willing to trust the bloom filter.
/// </summary>
public enum AvailabilityLookupMode
{
    /// <summary>
    /// Always confirm against the database. Required on any path that is about to claim the
    /// value: the local filter can lag another instance by up to one refresh interval, and a
    /// wrongly optimistic answer there turns a clean conflict into a unique-index violation.
    /// </summary>
    Authoritative = 0,

    /// <summary>
    /// Let the filter answer when it proves the value is absent. For read-only probes, where a
    /// briefly stale answer costs a late error message and nothing else.
    /// </summary>
    Advisory = 1,
}
