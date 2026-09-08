namespace backend.main.features.profile.contracts;

public enum EmailChangeStatus
{
    Changed,
    UserNotFound,
    Unchanged,
    Unavailable,
    /// <summary>
    /// The account's credentials were rotated after the change was requested, so the proof no
    /// longer authorises anything. Decided while holding the row lock, because a check made
    /// before the transaction is a guess about the past.
    /// </summary>
    Stale,
}

/// <param name="Status">Outcome of the change attempt.</param>
/// <param name="User">The user row after the change, when one was loaded.</param>
/// <param name="PreviousEmail">
/// The address released by a successful change, exactly as it was stored. Unlike a released
/// username it is freed immediately — there is no reservation table for emails — but callers still
/// need it to notify the old inbox and to re-point records that reference it.
/// </param>
public sealed record EmailChangeRecord(
    EmailChangeStatus Status,
    User? User = null,
    string? PreviousEmail = null
);
