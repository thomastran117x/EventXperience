namespace backend.main.features.clubs.versions.contracts.responses;

public sealed record ClubVersionDetailResponse(
    int VersionNumber,
    string ActionType,
    DateTime CreatedAt,
    int ActorUserId,
    string ActorRole,
    bool RollbackEligible,
    DateTime RollbackExpiresAt,
    int? RollbackSourceVersionNumber,
    IReadOnlyList<ClubVersionFieldChangeResponse> ChangedFields,
    ClubVersionSnapshotResponse Snapshot,
    string? ActorName = null,
    string? ActorUsername = null,
    string? ActorAvatar = null,
    // Appended rather than placed beside ActorUsername: these records are constructed
    // positionally, so inserting mid-list would silently rebind ActorAvatar to this parameter.
    string? ActorUsernameDisplay = null);
