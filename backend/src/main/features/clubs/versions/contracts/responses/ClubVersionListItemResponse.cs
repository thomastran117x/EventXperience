namespace backend.main.features.clubs.versions.contracts.responses;

public sealed record ClubVersionListItemResponse(
    int VersionNumber,
    string ActionType,
    DateTime CreatedAt,
    int ActorUserId,
    string ActorRole,
    bool RollbackEligible,
    DateTime RollbackExpiresAt,
    int? RollbackSourceVersionNumber,
    IReadOnlyList<ClubVersionFieldChangeResponse> ChangedFields,
    string? ActorName = null,
    string? ActorUsername = null,
    string? ActorAvatar = null,
    // Appended rather than placed beside ActorUsername: these records are constructed
    // positionally, so inserting mid-list would silently rebind ActorAvatar to this parameter.
    string? ActorUsernameDisplay = null);
