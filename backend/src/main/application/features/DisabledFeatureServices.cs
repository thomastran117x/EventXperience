using backend.main.features.clubs;
using backend.main.features.clubs.follow;
using backend.main.features.clubs.posts;
using backend.main.features.clubs.posts.search;
using backend.main.features.clubs.search;
using backend.main.features.events;
using backend.main.features.events.favourites;
using backend.main.features.events.favourites.contracts.responses;
using backend.main.features.events.invitations;
using backend.main.features.events.invitations.contracts.responses;
using backend.main.features.events.registration;
using backend.main.features.events.registration.contracts.requests;
using backend.main.features.events.registration.contracts.responses;
using backend.main.features.events.search;
using backend.main.features.events.series;
using backend.main.features.events.series.contracts.requests;
using backend.main.features.events.series.contracts.responses;
using backend.main.features.events.waitlist;
using backend.main.features.events.waitlist.contracts.requests;
using backend.main.features.events.waitlist.contracts.responses;
using backend.main.features.payment;
using backend.main.features.profile.contracts;
using backend.main.infrastructure.elasticsearch;
using backend.main.shared.exceptions.http;

namespace backend.main.application.features;

internal static class DisabledFeatureErrors
{
    public static NotAvailableException Create(string featureKey) =>
        new($"The '{featureKey}' feature is disabled.");
}

public sealed class DisabledFollowService : IFollowService
{
    public Task<FollowClub> GetFollowAsync(int id) => Task.FromException<FollowClub>(DisabledFeatureErrors.Create(FeatureFlagKeys.ClubsFollow));
    public Task<IEnumerable<FollowClub>> GetFollowsAsync(int page = 1, int pageSize = 20) => Task.FromException<IEnumerable<FollowClub>>(DisabledFeatureErrors.Create(FeatureFlagKeys.ClubsFollow));
    public Task<IEnumerable<FollowClub>> GetFollowsByUserAsync(int userId, int page = 1, int pageSize = 20) => Task.FromException<IEnumerable<FollowClub>>(DisabledFeatureErrors.Create(FeatureFlagKeys.ClubsFollow));
    public Task<IEnumerable<FollowClub>> GetFollowsByClubAsync(int clubId, int page = 1, int pageSize = 20) => Task.FromException<IEnumerable<FollowClub>>(DisabledFeatureErrors.Create(FeatureFlagKeys.ClubsFollow));
    public Task<(IReadOnlyList<FollowClub> Members, IReadOnlyDictionary<int, UserListRecord> Users, int TotalCount)> GetClubMembersAsync(int clubId, int page = 1, int pageSize = 20, string? search = null) => Task.FromException<(IReadOnlyList<FollowClub>, IReadOnlyDictionary<int, UserListRecord>, int)>(DisabledFeatureErrors.Create(FeatureFlagKeys.ClubsFollow));
    public Task<bool> IsMemberAsync(int clubId, int userId) => Task.FromResult(false);
    public Task AddMembershipAsync(int clubId, int userId) => Task.FromException(DisabledFeatureErrors.Create(FeatureFlagKeys.ClubsFollow));
    public Task RemoveMembershipAsync(int clubId, int userId) => Task.FromException(DisabledFeatureErrors.Create(FeatureFlagKeys.ClubsFollow));
}

public sealed class DisabledEventInvitationService : IEventInvitationService
{
    public Task<bool> HasAcceptedInvitationAccessAsync(int eventId, int userId) => Task.FromResult(false);
    public Task<IReadOnlyList<EventInvitationResponse>> CreateInvitationsAsync(int eventId, int actorUserId, string actorRole, IEnumerable<int> userIds, IEnumerable<string> emails, DateTime? expiresAt) => Task.FromException<IReadOnlyList<EventInvitationResponse>>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
    public Task<IReadOnlyList<EventInvitationResponse>> GetEventInvitationsAsync(int eventId, int actorUserId, string actorRole) => Task.FromException<IReadOnlyList<EventInvitationResponse>>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
    public Task<EventInvitationResponse> RevokeInvitationAsync(int eventId, int invitationId, int actorUserId, string actorRole) => Task.FromException<EventInvitationResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
    public Task<EventInvitationLinkResponse> CreateInvitationLinkAsync(int eventId, int actorUserId, string actorRole, int maxRedemptions, DateTime expiresAt) => Task.FromException<EventInvitationLinkResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
    public Task<IReadOnlyList<EventInvitationLinkResponse>> GetInvitationLinksAsync(int eventId, int actorUserId, string actorRole) => Task.FromException<IReadOnlyList<EventInvitationLinkResponse>>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
    public Task<EventInvitationLinkResponse> RevokeInvitationLinkAsync(int eventId, int linkId, int actorUserId, string actorRole) => Task.FromException<EventInvitationLinkResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
    public Task<EventInvitationResolveResponse> ResolveInvitationAsync(string token, int? userId = null, string? email = null) => Task.FromException<EventInvitationResolveResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
    public Task<EventInvitationDecisionResponse> AcceptInvitationAsync(string token, int userId, string userEmail) => Task.FromException<EventInvitationDecisionResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
    public Task<EventInvitationDecisionResponse> DeclineInvitationAsync(string token, int userId, string userEmail) => Task.FromException<EventInvitationDecisionResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
    public Task<EventInvitationDecisionResponse> AcceptInvitationByIdAsync(int invitationId, int userId, string userEmail) => Task.FromException<EventInvitationDecisionResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
    public Task<EventInvitationDecisionResponse> DeclineInvitationByIdAsync(int invitationId, int userId, string userEmail) => Task.FromException<EventInvitationDecisionResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
    public Task<IReadOnlyList<EventInvitationResponse>> GetMyInvitationsAsync(int userId, string userEmail) => Task.FromException<IReadOnlyList<EventInvitationResponse>>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
    public Task RelinkForEmailChangeAsync(int userId, string previousNormalizedEmail, string newNormalizedEmail) => Task.CompletedTask;
    public Task MarkInvitationDeliveryStatusAsync(int invitationId, EventInvitationDeliveryStatus status, string? errorMessage) => Task.FromException(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsInvitations));
}

public sealed class DisabledEventRegistrationService : IEventRegistrationService
{
    public Task RegisterAsync(int eventId, int userId, string userRole, RegisterEventRequest? request = null) => Task.FromException(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRegistration));
    public Task UnregisterAsync(int eventId, int userId, string userRole) => Task.FromException(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRegistration));
    public Task<EventRegistration> UpdateRegistrationAsync(int eventId, int userId, string userRole, UpdateRegistrationRequest request) => Task.FromException<EventRegistration>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRegistration));
    public Task<bool> IsRegisteredAsync(int eventId, int userId, string userRole) => Task.FromException<bool>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRegistration));
    public Task<EventRegistration?> GetMyRegistrationAsync(int eventId, int userId, string userRole) => Task.FromException<EventRegistration?>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRegistration));
    public Task<IEnumerable<EventRegistration>> GetRegistrationsByEventAsync(int eventId, int page = 1, int pageSize = 20) => Task.FromException<IEnumerable<EventRegistration>>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRegistration));
    public Task<IEnumerable<EventRegistration>> GetRegistrationsByUserAsync(int userId, int page = 1, int pageSize = 20) => Task.FromException<IEnumerable<EventRegistration>>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRegistration));
    public Task<BatchRegistrationResultResponse> BatchRegisterAsync(int userId, string userRole, IEnumerable<int> eventIds) => Task.FromException<BatchRegistrationResultResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRegistration));
    public Task<BatchRegistrationResultResponse> BatchUnregisterAsync(int userId, string userRole, IEnumerable<int> eventIds) => Task.FromException<BatchRegistrationResultResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRegistration));
}

public sealed class DisabledEventFavouriteService : IEventFavouriteService
{
    public Task<EventFavouriteResponse> FavouriteAsync(int eventId, int userId, string userRole) => Task.FromException<EventFavouriteResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsFavourites));
    public Task UnfavouriteAsync(int eventId, int userId) => Task.FromException(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsFavourites));
    public Task<EventFavouriteResponse> GetMyStatusAsync(int eventId, int userId) => Task.FromException<EventFavouriteResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsFavourites));
    public Task<IReadOnlyList<int>> GetFavouriteEventIdsAsync(int userId) => Task.FromException<IReadOnlyList<int>>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsFavourites));
    public Task<IReadOnlyList<PinnedEventResponse>> GetMyPinnedAsync(int userId, string userRole) => Task.FromException<IReadOnlyList<PinnedEventResponse>>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsFavourites));
}

public sealed class DisabledEventWaitlistService : IEventWaitlistService
{
    public Task<EventWaitlistEntryResponse> JoinAsync(int eventId, int userId, string userRole, JoinWaitlistRequest? request = null) => Task.FromException<EventWaitlistEntryResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsWaitlist));
    public Task LeaveAsync(int eventId, int userId, string userRole) => Task.FromException(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsWaitlist));
    public Task<MyWaitlistStatusResponse> GetMyStatusAsync(int eventId, int userId, string userRole) => Task.FromException<MyWaitlistStatusResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsWaitlist));
    public Task<(IReadOnlyList<EventWaitlistEntryResponse> Entries, int TotalCount)> GetEventWaitlistAsync(int eventId, int actorUserId, string actorRole, int page = 1, int pageSize = 20) => Task.FromException<(IReadOnlyList<EventWaitlistEntryResponse>, int)>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsWaitlist));
    public Task RemoveEntryAsync(int eventId, int entryId, int actorUserId, string actorRole) => Task.FromException(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsWaitlist));
    public Task<IReadOnlyList<WaitlistedEventResponse>> GetMyWaitlistsAsync(int userId, string userRole) => Task.FromException<IReadOnlyList<WaitlistedEventResponse>>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsWaitlist));
    public Task<WaitlistPromotionResultResponse> PromoteNextAsync(int eventId, int actorUserId, string actorRole) => Task.FromException<WaitlistPromotionResultResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsWaitlist));
}

public sealed class DisabledEventSeriesService : IEventSeriesService
{
    public Task<EventSeriesPreviewResponse> PreviewAsync(int clubId, int userId, string userRole, EventRecurrenceRuleRequest rule) => Task.FromException<EventSeriesPreviewResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRecurrence));
    public Task<EventSeriesResponse> CreateFromDraftAsync(int templateEventId, int userId, string userRole, CreateEventSeriesRequest request) => Task.FromException<EventSeriesResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRecurrence));
    public Task<EventSeriesResponse> GetAsync(int seriesId, int userId, string userRole) => Task.FromException<EventSeriesResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRecurrence));
    public Task<(IReadOnlyList<EventSeriesSummaryResponse> Series, int TotalCount)> GetByClubAsync(int clubId, int userId, string userRole, int page, int pageSize) => Task.FromException<(IReadOnlyList<EventSeriesSummaryResponse>, int)>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRecurrence));
    public Task<EventSeriesResponse> ExtendAsync(int seriesId, int userId, string userRole, ExtendEventSeriesRequest request) => Task.FromException<EventSeriesResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRecurrence));
    public Task<EventSeriesBulkResultResponse> PublishAsync(int seriesId, int userId, string userRole) => Task.FromException<EventSeriesBulkResultResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRecurrence));
    public Task<EventSeriesBulkResultResponse> UpdateFutureOccurrencesAsync(int seriesId, int userId, string userRole, UpdateFutureOccurrencesRequest request) => Task.FromException<EventSeriesBulkResultResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRecurrence));
    public Task<EventSeriesBulkResultResponse> CancelAsync(int seriesId, int userId, string userRole, CancelEventSeriesRequest request) => Task.FromException<EventSeriesBulkResultResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRecurrence));
    public Task<EventSeriesBulkResultResponse> DeleteAsync(int seriesId, int userId, string userRole, DeleteEventSeriesRequest request) => Task.FromException<EventSeriesBulkResultResponse>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRecurrence));
    public Task<Events> DetachOccurrenceAsync(int eventId, int userId, string userRole) => Task.FromException<Events>(DisabledFeatureErrors.Create(FeatureFlagKeys.EventsRecurrence));
}

/// <summary>
/// Unlike every other stub in this file, this one MUST NOT throw. It is injected into
/// EventRegistrationService.UnregisterAsync and EventsService.UpdateEvent, so throwing here
/// would break unregistration and event editing whenever the waitlist flag is off.
/// </summary>
public sealed class DisabledEventWaitlistPromoter : IEventWaitlistPromoter
{
    public Task<IReadOnlyList<WaitlistPromotion>> PromoteWithinTransactionAsync(int eventId, DateTime nowUtc) => Task.FromResult<IReadOnlyList<WaitlistPromotion>>([]);
    public Task<int> PromoteStandaloneAsync(int eventId) => Task.FromResult(0);
    public Task PublishPromotionEmailsAsync(IReadOnlyList<WaitlistPromotion> promotions, int eventId, string? eventName, DateTime? startsAtUtc) => Task.CompletedTask;
    public Task InvalidateForPromotedAsync(IReadOnlyList<WaitlistPromotion> promotions, int eventId) => Task.CompletedTask;
}

public sealed class DisabledPaymentService : IPaymentService
{
    public Task<Payment> CreatePaymentSession(int userId, string userRole, int eventId, string? idempotencyKey = null) => Task.FromException<Payment>(DisabledFeatureErrors.Create(FeatureFlagKeys.Payment));
    public Task<Payment> GetPayment(int paymentId) => Task.FromException<Payment>(DisabledFeatureErrors.Create(FeatureFlagKeys.Payment));
    public Task<List<Payment>> GetPaymentsByUser(int userId, int page = 1, int pageSize = 20) => Task.FromException<List<Payment>>(DisabledFeatureErrors.Create(FeatureFlagKeys.Payment));
    public Task HandleWebhook(string payload, string signature) => Task.FromException(DisabledFeatureErrors.Create(FeatureFlagKeys.Payment));
    public Task<Payment> RefundPayment(int paymentId, int requestingUserId) => Task.FromException<Payment>(DisabledFeatureErrors.Create(FeatureFlagKeys.Payment));
}

public sealed class DisabledEventSearchService : IEventSearchService
{
    public Task EnsureIndexAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteIndexAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task IndexAsync(EventDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(int eventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task BulkIndexAsync(IEnumerable<EventDocument> documents, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<EventSearchResult> SearchAsync(EventSearchCriteria criteria) => Task.FromException<EventSearchResult>(new ElasticsearchDisabledException("Event search is disabled by feature flag."));
}

public sealed class DisabledClubSearchService : IClubSearchService
{
    public Task EnsureIndexAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteIndexAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task IndexAsync(ClubDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(int clubId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task BulkIndexAsync(IEnumerable<ClubDocument> documents, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ClubSearchResult> SearchAsync(ClubSearchCriteria criteria) => Task.FromException<ClubSearchResult>(new ElasticsearchDisabledException("Club search is disabled by feature flag."));
}

public sealed class DisabledClubPostSearchService : IClubPostSearchService
{
    public Task EnsureIndexAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteIndexAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task IndexAsync(ClubPostDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(int postId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task BulkIndexAsync(IEnumerable<ClubPostDocument> documents, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<(List<int> Ids, int TotalCount)> SearchByClubAsync(int clubId, string search, PostSortBy sortBy, int page, int pageSize) => Task.FromException<(List<int>, int)>(new ElasticsearchDisabledException("Club post search is disabled by feature flag."));
    public Task<(List<int> Ids, int TotalCount)> SearchAllAsync(string search, PostSortBy sortBy, int page, int pageSize) => Task.FromException<(List<int>, int)>(new ElasticsearchDisabledException("Club post search is disabled by feature flag."));
}

public sealed class DisabledEventSearchOutboxWriter : IEventSearchOutboxWriter
{
    public void StageUpsert(Events ev)
    {
    }
    public void StageSync(Events ev)
    {
    }
    public void StageDelete(int eventId)
    {
    }
}

public sealed class DisabledClubSearchOutboxWriter : IClubSearchOutboxWriter
{
    public void StageUpsert(Club club)
    {
    }
    public void StageDelete(int clubId)
    {
    }
}

public sealed class DisabledClubPostSearchOutboxWriter : IClubPostSearchOutboxWriter
{
    public void StageUpsert(ClubPost post)
    {
    }
    public void StageDelete(int postId)
    {
    }
}

public sealed class DisabledEventReindexService : IEventReindexService
{
    public Task<int> ReindexAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<int>(DisabledFeatureErrors.Create(FeatureFlagKeys.SearchReindex));
}

public sealed class DisabledClubReindexService : IClubReindexService
{
    public Task<int> ReindexAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<int>(DisabledFeatureErrors.Create(FeatureFlagKeys.SearchReindex));
}

public sealed class DisabledClubPostReindexService : IClubPostReindexService
{
    public Task<int> ReindexAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<int>(DisabledFeatureErrors.Create(FeatureFlagKeys.SearchReindex));
}


