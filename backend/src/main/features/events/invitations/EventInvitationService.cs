using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

using backend.main.features.cache;
using backend.main.features.clubs;
using backend.main.features.events.contracts.responses;
using backend.main.features.events.invitations.contracts.responses;
using backend.main.features.profile;
using backend.main.infrastructure.database.core;
using backend.main.shared.exceptions.http;
using backend.main.shared.providers;
using backend.main.shared.providers.messages;

using Microsoft.EntityFrameworkCore;

namespace backend.main.features.events.invitations;

public sealed class EventInvitationService : IEventInvitationService
{
    private readonly AppDatabaseContext _db;
    private readonly IClubService _clubService;
    private readonly IUserRepository _userRepository;
    private readonly IPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly IRefreshAheadCache _refreshCache;

    private static readonly TimeSpan MyInvitationsTTL = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan EventInvitationsTTL = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InvitationLinksTTL = TimeSpan.FromMinutes(5);

    private static string GetMyInvitationsCacheKey(int userId, string normalizedEmail) =>
        $"invitation:user:{userId}:{normalizedEmail}";

    private static string GetEventInvitationsCacheKey(int eventId) =>
        $"invitation:event:{eventId}:list";

    private static string GetInvitationLinksCacheKey(int eventId) =>
        $"invitation:event:{eventId}:links";

    public EventInvitationService(
        AppDatabaseContext db,
        IClubService clubService,
        IUserRepository userRepository,
        IPublisher publisher,
        TimeProvider timeProvider,
        IRefreshAheadCache refreshCache)
    {
        _db = db;
        _clubService = clubService;
        _userRepository = userRepository;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _refreshCache = refreshCache;
    }

    public async Task<bool> HasAcceptedInvitationAccessAsync(int eventId, int userId)
    {
        return await _db.EventInvitations
            .AsNoTracking()
            .AnyAsync(i =>
                i.EventId == eventId &&
                i.RecipientUserId == userId &&
                i.LifecycleStatus == EventInvitationLifecycleStatus.Accepted);
    }

    public async Task<IReadOnlyList<EventInvitationResponse>> CreateInvitationsAsync(
        int eventId,
        int actorUserId,
        string actorRole,
        IEnumerable<int> userIds,
        IEnumerable<string> emails,
        DateTime? expiresAt)
    {
        var ev = await GetManageablePrivateEventAsync(eventId, actorUserId, actorRole);
        var now = GetUtcNow();
        ValidateExpiry(expiresAt, now);

        var normalizedUserIds = userIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var normalizedEmails = NormalizeEmails(emails);

        if (normalizedUserIds.Count == 0 && normalizedEmails.Count == 0)
            throw new BadRequestException("At least one user ID or email address is required.");

        var createdOrExisting = new List<EventInvitation>();
        var queuedEmails = new List<(EventInvitation Invitation, string Token, string RecipientEmail)>();

        foreach (var userId in normalizedUserIds)
        {
            var user = await _userRepository.GetUserAsync(userId)
                ?? throw new ResourceNotFoundException($"User with ID {userId} is not found.");

            var existing = await FindReusableDirectUserInvitationAsync(eventId, userId, now);
            if (existing != null)
            {
                createdOrExisting.Add(existing);
                continue;
            }

            var token = GenerateOpaqueToken();
            var invitation = new EventInvitation
            {
                EventId = eventId,
                RecipientUserId = userId,
                RecipientEmail = user.Email,
                RecipientEmailNormalized = EmailPolicy.Normalize(user.Email),
                SourceType = EventInvitationSource.DirectUser,
                LifecycleStatus = EventInvitationLifecycleStatus.Pending,
                DeliveryStatus = EventInvitationDeliveryStatus.Queued,
                ClaimTokenHash = ComputeTokenHash(token),
                ExpiresAt = expiresAt,
                CreatedByUserId = actorUserId,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.EventInvitations.Add(invitation);
            createdOrExisting.Add(invitation);
            queuedEmails.Add((invitation, token, user.Email));
        }

        foreach (var email in normalizedEmails)
        {
            var existing = await FindReusableDirectEmailInvitationAsync(eventId, email.Normalized, now);
            if (existing != null)
            {
                createdOrExisting.Add(existing);
                continue;
            }

            var token = GenerateOpaqueToken();
            var invitation = new EventInvitation
            {
                EventId = eventId,
                RecipientEmail = email.Original,
                RecipientEmailNormalized = email.Normalized,
                SourceType = EventInvitationSource.DirectEmail,
                LifecycleStatus = EventInvitationLifecycleStatus.Pending,
                DeliveryStatus = EventInvitationDeliveryStatus.Queued,
                ClaimTokenHash = ComputeTokenHash(token),
                ExpiresAt = expiresAt,
                CreatedByUserId = actorUserId,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.EventInvitations.Add(invitation);
            createdOrExisting.Add(invitation);
            queuedEmails.Add((invitation, token, email.Original));
        }

        await _db.SaveChangesAsync();
        foreach (var queuedEmail in queuedEmails)
        {
            await QueueInvitationEmailAsync(
                queuedEmail.Invitation,
                queuedEmail.Token,
                ev.Name,
                queuedEmail.RecipientEmail);
        }

        await _refreshCache.RemoveAsync(GetEventInvitationsCacheKey(eventId));
        return await MapInvitationResponsesAsync(createdOrExisting, includeEvents: false);
    }

    public async Task<IReadOnlyList<EventInvitationResponse>> GetEventInvitationsAsync(int eventId, int actorUserId, string actorRole)
    {
        await GetManageablePrivateEventAsync(eventId, actorUserId, actorRole);

        return await _refreshCache.GetOrSetAsync(
            GetEventInvitationsCacheKey(eventId),
            async () =>
            {
                var invitations = await _db.EventInvitations
                    .AsNoTracking()
                    .Where(i => i.EventId == eventId)
                    .OrderByDescending(i => i.CreatedAt)
                    .ToListAsync();

                return (IReadOnlyList<EventInvitationResponse>)await MapInvitationResponsesAsync(invitations, includeEvents: true);
            },
            EventInvitationsTTL) ?? [];
    }

    public async Task<EventInvitationResponse> RevokeInvitationAsync(int eventId, int invitationId, int actorUserId, string actorRole)
    {
        await GetManageablePrivateEventAsync(eventId, actorUserId, actorRole);

        var invitation = await _db.EventInvitations
            .FirstOrDefaultAsync(i => i.Id == invitationId && i.EventId == eventId)
            ?? throw new ResourceNotFoundException($"Invitation {invitationId} was not found.");

        if (invitation.LifecycleStatus != EventInvitationLifecycleStatus.Revoked)
        {
            invitation.LifecycleStatus = EventInvitationLifecycleStatus.Revoked;
            invitation.RevokedAtUtc = GetUtcNow();
            invitation.RevokedByUserId = actorUserId;
            invitation.UpdatedAt = invitation.RevokedAtUtc.Value;
            await _db.SaveChangesAsync();
        }

        await _refreshCache.RemoveAsync(GetEventInvitationsCacheKey(eventId));
        return (await MapInvitationResponsesAsync([invitation], includeEvents: true)).Single();
    }

    public async Task<EventInvitationLinkResponse> CreateInvitationLinkAsync(
        int eventId,
        int actorUserId,
        string actorRole,
        int maxRedemptions,
        DateTime expiresAt)
    {
        await GetManageablePrivateEventAsync(eventId, actorUserId, actorRole);

        if (maxRedemptions < 1)
            throw new BadRequestException("maxRedemptions must be at least 1.");

        var now = GetUtcNow();
        if (expiresAt <= now)
            throw new BadRequestException("Invitation links must expire in the future.");

        var token = GenerateOpaqueToken();
        var entity = new EventInvitationLink
        {
            EventId = eventId,
            TokenHash = ComputeTokenHash(token),
            ExpiresAt = expiresAt,
            MaxRedemptions = maxRedemptions,
            RedemptionCount = 0,
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.EventInvitationLinks.Add(entity);
        await _db.SaveChangesAsync();

        await _refreshCache.RemoveAsync(GetInvitationLinksCacheKey(eventId));
        return MapLinkResponse(entity, token);
    }

    public async Task<IReadOnlyList<EventInvitationLinkResponse>> GetInvitationLinksAsync(int eventId, int actorUserId, string actorRole)
    {
        await GetManageablePrivateEventAsync(eventId, actorUserId, actorRole);

        return await _refreshCache.GetOrSetAsync(
            GetInvitationLinksCacheKey(eventId),
            async () => (IReadOnlyList<EventInvitationLinkResponse>)await _db.EventInvitationLinks
                .AsNoTracking()
                .Where(l => l.EventId == eventId)
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => MapLinkResponse(l, null))
                .ToListAsync(),
            InvitationLinksTTL) ?? [];
    }

    public async Task<EventInvitationLinkResponse> RevokeInvitationLinkAsync(int eventId, int linkId, int actorUserId, string actorRole)
    {
        await GetManageablePrivateEventAsync(eventId, actorUserId, actorRole);

        var link = await _db.EventInvitationLinks
            .FirstOrDefaultAsync(l => l.Id == linkId && l.EventId == eventId)
            ?? throw new ResourceNotFoundException($"Invitation link {linkId} was not found.");

        if (link.RevokedAtUtc == null)
        {
            link.RevokedAtUtc = GetUtcNow();
            link.RevokedByUserId = actorUserId;
            link.UpdatedAt = link.RevokedAtUtc.Value;
            await _db.SaveChangesAsync();
        }

        await _refreshCache.RemoveAsync(GetInvitationLinksCacheKey(eventId));
        return MapLinkResponse(link, null);
    }

    public async Task<EventInvitationResolveResponse> ResolveInvitationAsync(string token, int? userId = null, string? email = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new BadRequestException("A token is required.");

        var tokenHash = ComputeTokenHash(token);
        var now = GetUtcNow();
        var normalizedEmail = NormalizeOptionalEmail(email);

        var invitation = await _db.EventInvitations
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.ClaimTokenHash == tokenHash);

        if (invitation != null)
            return await ResolveDirectInvitationAsync(invitation, userId, normalizedEmail, now);

        var link = await _db.EventInvitationLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.TokenHash == tokenHash);

        if (link != null)
            return await ResolveLinkInvitationAsync(link, userId, normalizedEmail, now);

        return new EventInvitationResolveResponse
        {
            State = EventInvitationResolveState.Invalid.ToString(),
            RequiresAuthentication = false,
            CanAccept = false,
            CanDecline = false,
            SourceType = string.Empty
        };
    }

    public async Task<EventInvitationDecisionResponse> AcceptInvitationAsync(string token, int userId, string userEmail)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new BadRequestException("A token is required.");

        var normalizedEmail = EmailPolicy.Normalize(userEmail);
        var tokenHash = ComputeTokenHash(token);
        var now = GetUtcNow();

        // The context enables retry-on-failure, and that strategy rejects a
        // user-initiated transaction unless the whole unit runs through it.
        var strategy = _db.Database.CreateExecutionStrategy();
        var (resultInvitation, eventId) = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            var invitation = await _db.EventInvitations
                .FirstOrDefaultAsync(i => i.ClaimTokenHash == tokenHash);

            if (invitation != null)
            {
                EnsureDirectInvitationCanBeAccepted(invitation, userId, normalizedEmail, now);
                await EnsureEventAcceptsInvitationsAsync(invitation.EventId);
                invitation.LifecycleStatus = EventInvitationLifecycleStatus.Accepted;
                invitation.AcceptedAtUtc = now;
                invitation.AcceptedByUserId = userId;
                invitation.RecipientUserId ??= userId;
                invitation.RecipientEmail ??= userEmail;
                invitation.RecipientEmailNormalized ??= normalizedEmail;
                invitation.UpdatedAt = now;
                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
                return (invitation, invitation.EventId);
            }

            var link = await _db.EventInvitationLinks
                .FirstOrDefaultAsync(l => l.TokenHash == tokenHash)
                ?? throw new ResourceNotFoundException("Invitation was not found.");

            EnsureLinkCanBeAccepted(link, now);
            await EnsureEventAcceptsInvitationsAsync(link.EventId);

            var existingClaim = await _db.EventInvitations
                .FirstOrDefaultAsync(i =>
                    i.EventInvitationLinkId == link.Id &&
                    i.RecipientUserId == userId);

            if (existingClaim != null)
            {
                if (existingClaim.LifecycleStatus == EventInvitationLifecycleStatus.Accepted)
                {
                    await transaction.CommitAsync();
                    return (existingClaim, link.EventId);
                }

                if (existingClaim.LifecycleStatus == EventInvitationLifecycleStatus.Declined)
                    throw new ConflictException("This invitation has already been declined.");

                if (existingClaim.LifecycleStatus == EventInvitationLifecycleStatus.Revoked)
                    throw new GoneException("This invitation is no longer available.");
            }

            var acceptedClaim = new EventInvitation
            {
                EventId = link.EventId,
                RecipientUserId = userId,
                RecipientEmail = userEmail,
                RecipientEmailNormalized = normalizedEmail,
                SourceType = EventInvitationSource.LinkClaim,
                LifecycleStatus = EventInvitationLifecycleStatus.Accepted,
                DeliveryStatus = EventInvitationDeliveryStatus.Skipped,
                ExpiresAt = link.ExpiresAt,
                EventInvitationLinkId = link.Id,
                CreatedByUserId = link.CreatedByUserId,
                AcceptedByUserId = userId,
                AcceptedAtUtc = now,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.EventInvitations.Add(acceptedClaim);
            link.RedemptionCount += 1;
            link.UpdatedAt = now;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (acceptedClaim, link.EventId);
        });

        await _refreshCache.RemoveAsync(GetMyInvitationsCacheKey(userId, normalizedEmail));
        await _refreshCache.RemoveAsync(GetEventInvitationsCacheKey(eventId));
        return new EventInvitationDecisionResponse
        {
            Invitation = (await MapInvitationResponsesAsync([resultInvitation], includeEvents: true)).Single()
        };
    }

    public async Task<EventInvitationDecisionResponse> AcceptInvitationByIdAsync(int invitationId, int userId, string userEmail)
    {
        var invitation = await _db.EventInvitations
            .FirstOrDefaultAsync(i => i.Id == invitationId)
            ?? throw new ResourceNotFoundException($"Invitation {invitationId} was not found.");

        if (invitation.SourceType == EventInvitationSource.LinkClaim)
            throw new BadRequestException("Link-based invitations must be accepted from the invitation link.");

        var now = GetUtcNow();
        var normalizedEmail = EmailPolicy.Normalize(userEmail);
        EnsureDirectInvitationCanBeAccepted(invitation, userId, normalizedEmail, now);
        await EnsureEventAcceptsInvitationsAsync(invitation.EventId);

        invitation.LifecycleStatus = EventInvitationLifecycleStatus.Accepted;
        invitation.AcceptedAtUtc = now;
        invitation.AcceptedByUserId = userId;
        invitation.RecipientUserId ??= userId;
        invitation.RecipientEmail ??= userEmail;
        invitation.RecipientEmailNormalized ??= normalizedEmail;
        invitation.UpdatedAt = now;
        await _db.SaveChangesAsync();

        await _refreshCache.RemoveAsync(GetMyInvitationsCacheKey(userId, normalizedEmail));
        await _refreshCache.RemoveAsync(GetEventInvitationsCacheKey(invitation.EventId));
        return new EventInvitationDecisionResponse
        {
            Invitation = (await MapInvitationResponsesAsync([invitation], includeEvents: true)).Single()
        };
    }

    public async Task<EventInvitationDecisionResponse> DeclineInvitationAsync(string token, int userId, string userEmail)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new BadRequestException("A token is required.");

        var normalizedEmail = EmailPolicy.Normalize(userEmail);
        var tokenHash = ComputeTokenHash(token);
        var now = GetUtcNow();

        // The context enables retry-on-failure, and that strategy rejects a
        // user-initiated transaction unless the whole unit runs through it.
        var strategy = _db.Database.CreateExecutionStrategy();
        var (resultInvitation, eventId) = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            var invitation = await _db.EventInvitations
                .FirstOrDefaultAsync(i => i.ClaimTokenHash == tokenHash);

            if (invitation != null)
            {
                EnsureDirectInvitationCanBeDecided(invitation, userId, normalizedEmail, now);
                invitation.LifecycleStatus = EventInvitationLifecycleStatus.Declined;
                invitation.DeclinedAtUtc = now;
                invitation.DeclinedByUserId = userId;
                invitation.RecipientUserId ??= userId;
                invitation.RecipientEmail ??= userEmail;
                invitation.RecipientEmailNormalized ??= normalizedEmail;
                invitation.UpdatedAt = now;
                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
                return (invitation, invitation.EventId);
            }

            var link = await _db.EventInvitationLinks
                .FirstOrDefaultAsync(l => l.TokenHash == tokenHash)
                ?? throw new ResourceNotFoundException("Invitation was not found.");

            EnsureLinkCanBeAccepted(link, now);

            var existingDecision = await _db.EventInvitations
                .FirstOrDefaultAsync(i =>
                    i.EventInvitationLinkId == link.Id &&
                    i.RecipientUserId == userId);

            if (existingDecision != null)
            {
                await transaction.CommitAsync();
                return (existingDecision, link.EventId);
            }

            var declinedClaim = new EventInvitation
            {
                EventId = link.EventId,
                RecipientUserId = userId,
                RecipientEmail = userEmail,
                RecipientEmailNormalized = normalizedEmail,
                SourceType = EventInvitationSource.LinkClaim,
                LifecycleStatus = EventInvitationLifecycleStatus.Declined,
                DeliveryStatus = EventInvitationDeliveryStatus.Skipped,
                ExpiresAt = link.ExpiresAt,
                EventInvitationLinkId = link.Id,
                CreatedByUserId = link.CreatedByUserId,
                DeclinedByUserId = userId,
                DeclinedAtUtc = now,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.EventInvitations.Add(declinedClaim);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (declinedClaim, link.EventId);
        });

        await _refreshCache.RemoveAsync(GetMyInvitationsCacheKey(userId, normalizedEmail));
        await _refreshCache.RemoveAsync(GetEventInvitationsCacheKey(eventId));
        return new EventInvitationDecisionResponse
        {
            Invitation = (await MapInvitationResponsesAsync([resultInvitation], includeEvents: true)).Single()
        };
    }

    public async Task<EventInvitationDecisionResponse> DeclineInvitationByIdAsync(int invitationId, int userId, string userEmail)
    {
        var invitation = await _db.EventInvitations
            .FirstOrDefaultAsync(i => i.Id == invitationId)
            ?? throw new ResourceNotFoundException($"Invitation {invitationId} was not found.");

        if (invitation.SourceType == EventInvitationSource.LinkClaim)
            throw new BadRequestException("Link-based invitations must be declined from the invitation link.");

        var now = GetUtcNow();
        var normalizedEmail = EmailPolicy.Normalize(userEmail);
        EnsureDirectInvitationCanBeDecided(invitation, userId, normalizedEmail, now);

        invitation.LifecycleStatus = EventInvitationLifecycleStatus.Declined;
        invitation.DeclinedAtUtc = now;
        invitation.DeclinedByUserId = userId;
        invitation.RecipientUserId ??= userId;
        invitation.RecipientEmail ??= userEmail;
        invitation.RecipientEmailNormalized ??= normalizedEmail;
        invitation.UpdatedAt = now;
        await _db.SaveChangesAsync();

        await _refreshCache.RemoveAsync(GetMyInvitationsCacheKey(userId, normalizedEmail));
        await _refreshCache.RemoveAsync(GetEventInvitationsCacheKey(invitation.EventId));
        return new EventInvitationDecisionResponse
        {
            Invitation = (await MapInvitationResponsesAsync([invitation], includeEvents: true)).Single()
        };
    }

    public async Task<IReadOnlyList<EventInvitationResponse>> GetMyInvitationsAsync(int userId, string userEmail)
    {
        var normalizedEmail = EmailPolicy.Normalize(userEmail);

        return await _refreshCache.GetOrSetAsync(
            GetMyInvitationsCacheKey(userId, normalizedEmail),
            async () =>
            {
                var now = GetUtcNow();

                var invitations = await _db.EventInvitations
                    .AsNoTracking()
                    .Where(i =>
                        (i.RecipientUserId == userId ||
                         (i.RecipientUserId == null && i.RecipientEmailNormalized == normalizedEmail)) &&
                        i.LifecycleStatus != EventInvitationLifecycleStatus.Revoked)
                    .OrderByDescending(i => i.CreatedAt)
                    .ToListAsync();

                var filtered = invitations
                    .Where(i =>
                        GetEffectiveStatus(i, now) == EventInvitationEffectiveStatus.Pending ||
                        GetEffectiveStatus(i, now) == EventInvitationEffectiveStatus.Accepted)
                    .ToList();

                return (IReadOnlyList<EventInvitationResponse>)await MapInvitationResponsesAsync(filtered, includeEvents: true);
            },
            MyInvitationsTTL) ?? [];
    }

    public async Task MarkInvitationDeliveryStatusAsync(int invitationId, EventInvitationDeliveryStatus status, string? errorMessage)
    {
        var invitation = await _db.EventInvitations.FirstOrDefaultAsync(i => i.Id == invitationId);
        if (invitation == null)
            return;

        invitation.DeliveryStatus = status;
        invitation.DeliveryError = string.IsNullOrWhiteSpace(errorMessage)
            ? null
            : errorMessage.Trim();
        invitation.UpdatedAt = GetUtcNow();
        await _db.SaveChangesAsync();
    }

    private async Task<EventInvitationResolveResponse> ResolveDirectInvitationAsync(
        EventInvitation invitation,
        int? userId,
        string? normalizedEmail,
        DateTime now)
    {
        var effectiveStatus = GetEffectiveStatus(invitation, now);
        var summary = await BuildEventSummaryAsync(invitation.EventId);

        if (effectiveStatus == EventInvitationEffectiveStatus.Revoked)
            return BuildResolveResponse(EventInvitationResolveState.Revoked, invitation, summary, false, false);

        if (effectiveStatus == EventInvitationEffectiveStatus.Expired)
            return BuildResolveResponse(EventInvitationResolveState.Expired, invitation, summary, false, false);

        if (effectiveStatus == EventInvitationEffectiveStatus.Accepted)
            return BuildResolveResponse(EventInvitationResolveState.AlreadyAccepted, invitation, summary, false, false);

        if (effectiveStatus == EventInvitationEffectiveStatus.Declined)
            return BuildResolveResponse(EventInvitationResolveState.Declined, invitation, summary, false, false);

        if (!userId.HasValue)
            return BuildResolveResponse(EventInvitationResolveState.LoginRequired, invitation, summary, true, false);

        if (!MatchesRecipient(invitation, userId.Value, normalizedEmail))
            return BuildResolveResponse(EventInvitationResolveState.Invalid, invitation, summary, false, false);

        return BuildResolveResponse(EventInvitationResolveState.AcceptAvailable, invitation, summary, true, true);
    }

    private async Task<EventInvitationResolveResponse> ResolveLinkInvitationAsync(
        EventInvitationLink link,
        int? userId,
        string? normalizedEmail,
        DateTime now)
    {
        var summary = await BuildEventSummaryAsync(link.EventId);
        if (link.RevokedAtUtc.HasValue)
        {
            return new EventInvitationResolveResponse
            {
                State = EventInvitationResolveState.Revoked.ToString(),
                RequiresAuthentication = false,
                CanAccept = false,
                CanDecline = false,
                SourceType = EventInvitationSource.LinkClaim.ToString(),
                ExpiresAt = link.ExpiresAt,
                Event = summary
            };
        }

        if (link.ExpiresAt <= now || link.RedemptionCount >= link.MaxRedemptions)
        {
            return new EventInvitationResolveResponse
            {
                State = EventInvitationResolveState.Expired.ToString(),
                RequiresAuthentication = false,
                CanAccept = false,
                CanDecline = false,
                SourceType = EventInvitationSource.LinkClaim.ToString(),
                ExpiresAt = link.ExpiresAt,
                Event = summary
            };
        }

        if (!userId.HasValue || string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return new EventInvitationResolveResponse
            {
                State = EventInvitationResolveState.LoginRequired.ToString(),
                RequiresAuthentication = true,
                CanAccept = false,
                CanDecline = false,
                SourceType = EventInvitationSource.LinkClaim.ToString(),
                ExpiresAt = link.ExpiresAt,
                Event = summary
            };
        }

        var existing = await _db.EventInvitations
            .AsNoTracking()
            .FirstOrDefaultAsync(i =>
                i.EventInvitationLinkId == link.Id &&
                i.RecipientUserId == userId.Value);

        if (existing != null)
        {
            var state = existing.LifecycleStatus switch
            {
                EventInvitationLifecycleStatus.Accepted => EventInvitationResolveState.AlreadyAccepted,
                EventInvitationLifecycleStatus.Declined => EventInvitationResolveState.Declined,
                EventInvitationLifecycleStatus.Revoked => EventInvitationResolveState.Revoked,
                _ => EventInvitationResolveState.AcceptAvailable
            };

            return new EventInvitationResolveResponse
            {
                State = state.ToString(),
                RequiresAuthentication = false,
                CanAccept = state == EventInvitationResolveState.AcceptAvailable,
                CanDecline = state == EventInvitationResolveState.AcceptAvailable,
                SourceType = EventInvitationSource.LinkClaim.ToString(),
                ExpiresAt = link.ExpiresAt,
                Event = summary
            };
        }

        return new EventInvitationResolveResponse
        {
            State = EventInvitationResolveState.AcceptAvailable.ToString(),
            RequiresAuthentication = false,
            CanAccept = true,
            CanDecline = true,
            SourceType = EventInvitationSource.LinkClaim.ToString(),
            ExpiresAt = link.ExpiresAt,
            Event = summary
        };
    }

    private static EventInvitationResolveResponse BuildResolveResponse(
        EventInvitationResolveState state,
        EventInvitation invitation,
        EventInvitationSummaryEventResponse summary,
        bool canAccept,
        bool canDecline)
    {
        return new EventInvitationResolveResponse
        {
            State = state.ToString(),
            RequiresAuthentication = state == EventInvitationResolveState.LoginRequired,
            CanAccept = canAccept,
            CanDecline = canDecline,
            SourceType = invitation.SourceType.ToString(),
            ExpiresAt = invitation.ExpiresAt,
            Event = summary
        };
    }

    private async Task QueueInvitationEmailAsync(EventInvitation invitation, string token, string eventName, string email)
    {
        var message = new EmailMessage
        {
            Type = EmailMessageType.EventInvite,
            Email = email,
            Token = token,
            EventInvitationId = invitation.Id == 0 ? null : invitation.Id,
            EventName = eventName
        };

        await _publisher.PublishAsync(NotificationTopics.Email, message);
    }

    private async Task<Events> GetManageablePrivateEventAsync(int eventId, int actorUserId, string actorRole)
    {
        var ev = await _db.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId)
            ?? throw new ResourceNotFoundException($"Event {eventId} not found.");

        if (!ev.isPrivate)
            throw new BadRequestException("Invitations are only supported for private events.");

        if (!EventLifecyclePolicy.AllowsInvitations(ev))
            throw new BadRequestException("Invitations are only supported for published private events.");

        if (!await _clubService.CanManageClubAsync(ev.ClubId, actorUserId, actorRole))
            throw new ForbiddenException("Not allowed");

        return ev;
    }

    private async Task<EventInvitation?> FindReusableDirectUserInvitationAsync(int eventId, int userId, DateTime now)
    {
        return await _db.EventInvitations
            .FirstOrDefaultAsync(i =>
                i.EventId == eventId &&
                i.RecipientUserId == userId &&
                i.SourceType == EventInvitationSource.DirectUser &&
                (i.LifecycleStatus == EventInvitationLifecycleStatus.Accepted ||
                 (i.LifecycleStatus == EventInvitationLifecycleStatus.Pending &&
                  (!i.ExpiresAt.HasValue || i.ExpiresAt > now))));
    }

    private async Task<EventInvitation?> FindReusableDirectEmailInvitationAsync(int eventId, string normalizedEmail, DateTime now)
    {
        return await _db.EventInvitations
            .FirstOrDefaultAsync(i =>
                i.EventId == eventId &&
                i.RecipientEmailNormalized == normalizedEmail &&
                i.SourceType == EventInvitationSource.DirectEmail &&
                (i.LifecycleStatus == EventInvitationLifecycleStatus.Accepted ||
                 (i.LifecycleStatus == EventInvitationLifecycleStatus.Pending &&
                  (!i.ExpiresAt.HasValue || i.ExpiresAt > now))));
    }

    private async Task<IReadOnlyList<EventInvitationResponse>> MapInvitationResponsesAsync(
        IEnumerable<EventInvitation> invitations,
        bool includeEvents)
    {
        var items = invitations.ToList();
        Dictionary<int, EventInvitationSummaryEventResponse>? summaries = null;

        if (includeEvents)
        {
            summaries = await LoadEventSummariesAsync(items.Select(i => i.EventId));
        }

        var now = GetUtcNow();
        return items.Select(i => new EventInvitationResponse
        {
            Id = i.Id,
            EventId = i.EventId,
            RecipientUserId = i.RecipientUserId,
            RecipientEmail = i.RecipientEmail,
            SourceType = i.SourceType.ToString(),
            LifecycleStatus = i.LifecycleStatus.ToString(),
            EffectiveStatus = GetEffectiveStatus(i, now).ToString(),
            DeliveryStatus = i.DeliveryStatus.ToString(),
            ExpiresAt = i.ExpiresAt,
            AcceptedAtUtc = i.AcceptedAtUtc,
            DeclinedAtUtc = i.DeclinedAtUtc,
            RevokedAtUtc = i.RevokedAtUtc,
            EventInvitationLinkId = i.EventInvitationLinkId,
            DeliveryError = i.DeliveryError,
            CreatedAt = i.CreatedAt,
            UpdatedAt = i.UpdatedAt,
            Event = summaries != null && summaries.TryGetValue(i.EventId, out var summary)
                ? summary
                : null
        }).ToList();
    }

    private async Task<Dictionary<int, EventInvitationSummaryEventResponse>> LoadEventSummariesAsync(IEnumerable<int> eventIds)
    {
        var ids = eventIds.Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var events = await _db.Events
            .AsNoTracking()
            .Include(e => e.Images)
            .Where(e => ids.Contains(e.Id))
            .ToListAsync();

        var clubs = await _db.Clubs
            .AsNoTracking()
            .Where(c => events.Select(e => e.ClubId).Distinct().Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);

        return events.ToDictionary(
            e => e.Id,
            e => MapSummaryEvent(e, clubs.TryGetValue(e.ClubId, out var club) ? club : null));
    }

    private async Task<EventInvitationSummaryEventResponse> BuildEventSummaryAsync(int eventId)
    {
        var events = await LoadEventSummariesAsync([eventId]);
        return events.TryGetValue(eventId, out var summary)
            ? summary
            : throw new ResourceNotFoundException($"Event {eventId} not found.");
    }

    private static EventInvitationSummaryEventResponse MapSummaryEvent(Events ev, Club? club)
    {
        return new EventInvitationSummaryEventResponse
        {
            Id = ev.Id,
            Name = ev.Name ?? string.Empty,
            Description = ev.Description ?? string.Empty,
            Location = ev.Location ?? string.Empty,
            IsPrivate = ev.isPrivate,
            RegisterCost = ev.registerCost,
            MaxParticipants = ev.maxParticipants,
            RegistrationCount = ev.RegistrationCount,
            StartTime = ev.StartTime ?? ev.CreatedAt,
            EndTime = ev.EndTime,
            LifecycleState = ev.LifecycleState.ToString(),
            Status = EventMapper.ResolveStatus(ev).ToString(),
            Category = ev.Category.ToString(),
            ImageUrls = ev.Images
                .OrderBy(i => i.SortOrder)
                .Select(i => i.ImageUrl)
                .ToList(),
            Club = club == null ? null : EventMapper.MapClubToResponse(club)
        };
    }

    private static EventInvitationLinkResponse MapLinkResponse(EventInvitationLink link, string? rawToken)
    {
        return new EventInvitationLinkResponse
        {
            Id = link.Id,
            EventId = link.EventId,
            ShareUrl = rawToken == null
                ? null
                : $"/events/invite?token={Uri.EscapeDataString(rawToken)}",
            ExpiresAt = link.ExpiresAt,
            MaxRedemptions = link.MaxRedemptions,
            RedemptionCount = link.RedemptionCount,
            IsRevoked = link.RevokedAtUtc.HasValue,
            RevokedAtUtc = link.RevokedAtUtc,
            CreatedAt = link.CreatedAt,
            UpdatedAt = link.UpdatedAt
        };
    }

    private static EventInvitationEffectiveStatus GetEffectiveStatus(EventInvitation invitation, DateTime now)
    {
        if (invitation.LifecycleStatus == EventInvitationLifecycleStatus.Revoked)
            return EventInvitationEffectiveStatus.Revoked;

        if (invitation.LifecycleStatus == EventInvitationLifecycleStatus.Accepted)
            return EventInvitationEffectiveStatus.Accepted;

        if (invitation.LifecycleStatus == EventInvitationLifecycleStatus.Declined)
            return EventInvitationEffectiveStatus.Declined;

        if (invitation.ExpiresAt.HasValue && invitation.ExpiresAt <= now)
            return EventInvitationEffectiveStatus.Expired;

        return EventInvitationEffectiveStatus.Pending;
    }

    private static bool MatchesRecipient(EventInvitation invitation, int userId, string? normalizedEmail)
    {
        if (invitation.RecipientUserId.HasValue)
            return invitation.RecipientUserId.Value == userId;

        return !string.IsNullOrWhiteSpace(normalizedEmail) &&
               string.Equals(invitation.RecipientEmailNormalized, normalizedEmail, StringComparison.Ordinal);
    }

    private async Task EnsureEventAcceptsInvitationsAsync(int eventId)
    {
        var ev = await _db.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId);

        if (ev == null || ev.LifecycleState != EventLifecycleState.Published)
            throw new ConflictException("Invitations can only be accepted for published events.");
    }

    private static void EnsureDirectInvitationCanBeAccepted(
        EventInvitation invitation,
        int userId,
        string normalizedEmail,
        DateTime now)
    {
        var effectiveStatus = GetEffectiveStatus(invitation, now);
        if (effectiveStatus == EventInvitationEffectiveStatus.Revoked)
            throw new GoneException("This invitation has been revoked.");

        if (effectiveStatus == EventInvitationEffectiveStatus.Expired)
            throw new GoneException("This invitation has expired.");

        if (effectiveStatus == EventInvitationEffectiveStatus.Accepted)
            return;

        if (effectiveStatus == EventInvitationEffectiveStatus.Declined)
            throw new ConflictException("This invitation has already been declined.");

        if (!MatchesRecipient(invitation, userId, normalizedEmail))
            throw new ForbiddenException("This invitation does not belong to the current user.");
    }

    private static void EnsureDirectInvitationCanBeDecided(
        EventInvitation invitation,
        int userId,
        string normalizedEmail,
        DateTime now)
    {
        var effectiveStatus = GetEffectiveStatus(invitation, now);
        if (effectiveStatus == EventInvitationEffectiveStatus.Revoked)
            throw new GoneException("This invitation has been revoked.");

        if (effectiveStatus == EventInvitationEffectiveStatus.Expired)
            throw new GoneException("This invitation has expired.");

        if (!MatchesRecipient(invitation, userId, normalizedEmail))
            throw new ForbiddenException("This invitation does not belong to the current user.");
    }

    private static void EnsureLinkCanBeAccepted(EventInvitationLink link, DateTime now)
    {
        if (link.RevokedAtUtc.HasValue)
            throw new GoneException("This invitation link has been revoked.");

        if (link.ExpiresAt <= now)
            throw new GoneException("This invitation link has expired.");

        if (link.RedemptionCount >= link.MaxRedemptions)
            throw new GoneException("This invitation link has expired.");
    }

    private static void ValidateExpiry(DateTime? expiresAt, DateTime now)
    {
        if (expiresAt.HasValue && expiresAt.Value <= now)
            throw new BadRequestException("Invitations must expire in the future.");
    }

    private static IReadOnlyList<(string Original, string Normalized)> NormalizeEmails(IEnumerable<string> emails)
    {
        return emails
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Select(email => email.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(email =>
            {
                try
                {
                    _ = new MailAddress(email);
                    return (Original: email, Normalized: EmailPolicy.Normalize(email));
                }
                catch (FormatException)
                {
                    throw new BadRequestException($"'{email}' is not a valid email address.");
                }
            })
            .ToList();
    }

    private static string? NormalizeOptionalEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : EmailPolicy.Normalize(email);

    private DateTime GetUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static string GenerateOpaqueToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string ComputeTokenHash(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}
