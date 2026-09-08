using backend.main.features.events.invitations.contracts.responses;

namespace backend.main.features.events.invitations;

public interface IEventInvitationService
{
    Task<bool> HasAcceptedInvitationAccessAsync(int eventId, int userId);
    Task<IReadOnlyList<EventInvitationResponse>> CreateInvitationsAsync(
        int eventId,
        int actorUserId,
        string actorRole,
        IEnumerable<int> userIds,
        IEnumerable<string> emails,
        DateTime? expiresAt);
    Task<IReadOnlyList<EventInvitationResponse>> GetEventInvitationsAsync(int eventId, int actorUserId, string actorRole);
    Task<EventInvitationResponse> RevokeInvitationAsync(int eventId, int invitationId, int actorUserId, string actorRole);
    Task<EventInvitationLinkResponse> CreateInvitationLinkAsync(
        int eventId,
        int actorUserId,
        string actorRole,
        int maxRedemptions,
        DateTime expiresAt);
    Task<IReadOnlyList<EventInvitationLinkResponse>> GetInvitationLinksAsync(int eventId, int actorUserId, string actorRole);
    Task<EventInvitationLinkResponse> RevokeInvitationLinkAsync(int eventId, int linkId, int actorUserId, string actorRole);
    Task<EventInvitationResolveResponse> ResolveInvitationAsync(string token, int? userId = null, string? email = null);
    Task<EventInvitationDecisionResponse> AcceptInvitationAsync(string token, int userId, string userEmail);
    Task<EventInvitationDecisionResponse> DeclineInvitationAsync(string token, int userId, string userEmail);
    Task<EventInvitationDecisionResponse> AcceptInvitationByIdAsync(int invitationId, int userId, string userEmail);
    Task<EventInvitationDecisionResponse> DeclineInvitationByIdAsync(int invitationId, int userId, string userEmail);
    Task<IReadOnlyList<EventInvitationResponse>> GetMyInvitationsAsync(int userId, string userEmail);
    /// <summary>
    /// Claims unclaimed invitations addressed to either side of an email change and drops the
    /// per-address cache entries. Invitations are matched by <c>RecipientEmailNormalized</c> when
    /// they have no recipient id yet, so without this an account would lose sight of every
    /// invitation sent to the address it just moved away from.
    /// </summary>
    Task RelinkForEmailChangeAsync(int userId, string previousNormalizedEmail, string newNormalizedEmail);

    Task MarkInvitationDeliveryStatusAsync(int invitationId, EventInvitationDeliveryStatus status, string? errorMessage);
}
