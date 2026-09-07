namespace backend.main.features.auth.contracts.responses;

/// <summary>
/// Result of an email availability probe.
/// </summary>
public sealed class EmailAvailabilityResponse
{
    /// <summary>The normalised form the address was checked as, so the client can show what it evaluated.</summary>
    public required string Email
    {
        get; set;
    }

    /// <summary>
    /// True when no account currently uses the address. Advisory only: the address is not reserved
    /// by asking, and signup can still fail with a conflict if someone registers it first.
    /// </summary>
    public required bool Available
    {
        get; set;
    }
}
