using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using backend.main.features.cache;
using backend.main.features.clubs.follow.invitations.contracts.responses;
using backend.main.features.profile;
using backend.main.features.profile.contracts;
using backend.main.infrastructure.database.core;
using backend.main.shared.exceptions.http;
using backend.main.shared.providers;
using backend.main.shared.providers.messages;

using Microsoft.EntityFrameworkCore;

namespace backend.main.features.clubs.follow.invitations
{
    /// <summary>
    /// Club member invitations, mirroring the staff-invite pattern for specific invites and the
    /// event-link pattern for shareable links. Specific invites live in Redis (TTL expiry); links
    /// live in the database. Both accept flows call <see cref="IClubService.GrantMembershipFromInvitationAsync"/>.
    /// </summary>
    public sealed class ClubMemberInvitationService : IClubMemberInvitationService
    {
        private readonly ICacheService _cache;
        private readonly IClubService _clubService;
        private readonly IFollowService _followService;
        private readonly IUserRepository _userRepository;
        private readonly IPublisher _publisher;
        private readonly TimeProvider _timeProvider;
        private readonly AppDatabaseContext _db;

        private static readonly TimeSpan InvitationTtl = TimeSpan.FromDays(14);

        public ClubMemberInvitationService(
            ICacheService cache,
            IClubService clubService,
            IFollowService followService,
            IUserRepository userRepository,
            IPublisher publisher,
            TimeProvider timeProvider,
            AppDatabaseContext db)
        {
            _cache = cache;
            _clubService = clubService;
            _followService = followService;
            _userRepository = userRepository;
            _publisher = publisher;
            _timeProvider = timeProvider;
            _db = db;
        }

        private static string TokenKey(string tokenHash) => $"club:memberinvite:token:{tokenHash}";

        private static string ClubIndexKey(int clubId) => $"club:memberinvite:club:{clubId}";

        // ---------------------------------------------------------------------
        // Specific invites (Redis + email)
        // ---------------------------------------------------------------------

        public async Task<ClubMemberInvitationResponse> CreateInvitationAsync(
            int clubId,
            int actorUserId,
            string actorRole,
            string identifier)
        {
            var club = await _clubService.GetClub(clubId);
            await EnsureCanManageAsync(clubId, actorUserId, actorRole);

            var user = await ResolveRecipientAsync(identifier);

            if (user.Id == club.UserId)
                throw new ConflictException("The club owner cannot be invited as a member.");

            if (await _followService.IsMemberAsync(clubId, user.Id))
                throw new ConflictException("This user is already a member of the club.");

            var now = GetUtcNow();
            var indexKey = ClubIndexKey(clubId);

            // Dedupe: reuse an outstanding, non-expired invite for the same user.
            var existingJson = await _cache.HashGetAsync(indexKey, user.Id.ToString());
            if (TryDeserialize(existingJson, out var existing))
            {
                if (existing!.ExpiresAtUtc > now)
                    return MapResponse(existing);

                await RemoveInviteAsync(clubId, existing);
            }

            var token = GenerateOpaqueToken();
            var tokenHash = ComputeTokenHash(token);
            var invite = new PendingClubMemberInvite
            {
                ClubId = clubId,
                RecipientUserId = user.Id,
                RecipientEmail = user.Email,
                CreatedByUserId = actorUserId,
                CreatedAtUtc = now,
                ExpiresAtUtc = now + InvitationTtl,
                TokenHash = tokenHash
            };

            var json = JsonSerializer.Serialize(invite);
            await _cache.SetValueAsync(TokenKey(tokenHash), json, InvitationTtl);
            await _cache.HashSetAsync(indexKey, user.Id.ToString(), json);
            // Safety net so the index hash never outlives its longest-lived field.
            await _cache.SetExpiryAsync(indexKey, InvitationTtl);

            await QueueInvitationEmailAsync(token, club.Name, user);

            return MapResponse(invite);
        }

        public async Task<IReadOnlyList<ClubMemberInvitationResponse>> GetClubInvitationsAsync(int clubId, int actorUserId, string actorRole)
        {
            _ = await _clubService.GetClub(clubId);
            await EnsureCanManageAsync(clubId, actorUserId, actorRole);

            var now = GetUtcNow();
            var entries = await _cache.HashGetAllAsync(ClubIndexKey(clubId));
            var results = new List<ClubMemberInvitationResponse>();

            foreach (var entry in entries)
            {
                if (!TryDeserialize(entry.Value, out var invite))
                {
                    await _cache.HashDeleteAsync(ClubIndexKey(clubId), entry.Key);
                    continue;
                }

                if (invite!.ExpiresAtUtc <= now)
                {
                    await RemoveInviteAsync(clubId, invite);
                    continue;
                }

                results.Add(MapResponse(invite));
            }

            return results
                .OrderByDescending(r => r.CreatedAtUtc)
                .ToList();
        }

        public async Task RevokeInvitationAsync(int clubId, int recipientUserId, int actorUserId, string actorRole)
        {
            _ = await _clubService.GetClub(clubId);
            await EnsureCanManageAsync(clubId, actorUserId, actorRole);

            var json = await _cache.HashGetAsync(ClubIndexKey(clubId), recipientUserId.ToString());
            if (!TryDeserialize(json, out var invite))
                throw new ResourceNotFoundException("Invitation was not found.");

            await RemoveInviteAsync(clubId, invite!);
        }

        // ---------------------------------------------------------------------
        // Invite links (DB, no email)
        // ---------------------------------------------------------------------

        public async Task<ClubInvitationLinkResponse> CreateLinkAsync(
            int clubId,
            int actorUserId,
            string actorRole,
            DateTime expiresAt,
            int? maxRedemptions)
        {
            _ = await _clubService.GetClub(clubId);
            await EnsureCanManageAsync(clubId, actorUserId, actorRole);

            var now = GetUtcNow();
            if (expiresAt <= now)
                throw new BadRequestException("Invitation links must expire in the future.");

            if (maxRedemptions.HasValue && maxRedemptions.Value < 1)
                throw new BadRequestException("maxRedemptions must be at least 1.");

            var token = GenerateOpaqueToken();
            var entity = new ClubInvitationLink
            {
                ClubId = clubId,
                TokenHash = ComputeTokenHash(token),
                ExpiresAt = expiresAt,
                MaxRedemptions = maxRedemptions,
                RedemptionCount = 0,
                CreatedByUserId = actorUserId,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.ClubInvitationLinks.Add(entity);
            await _db.SaveChangesAsync();

            return MapLinkResponse(entity, token);
        }

        public async Task<IReadOnlyList<ClubInvitationLinkResponse>> GetLinksAsync(int clubId, int actorUserId, string actorRole)
        {
            _ = await _clubService.GetClub(clubId);
            await EnsureCanManageAsync(clubId, actorUserId, actorRole);

            return await _db.ClubInvitationLinks
                .AsNoTracking()
                .Where(l => l.ClubId == clubId)
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => MapLinkResponse(l, null))
                .ToListAsync();
        }

        public async Task<ClubInvitationLinkResponse> RevokeLinkAsync(int clubId, int linkId, int actorUserId, string actorRole)
        {
            _ = await _clubService.GetClub(clubId);
            await EnsureCanManageAsync(clubId, actorUserId, actorRole);

            var link = await _db.ClubInvitationLinks
                .FirstOrDefaultAsync(l => l.Id == linkId && l.ClubId == clubId)
                ?? throw new ResourceNotFoundException($"Invitation link {linkId} was not found.");

            if (link.RevokedAtUtc == null)
            {
                link.RevokedAtUtc = GetUtcNow();
                link.RevokedByUserId = actorUserId;
                link.UpdatedAt = link.RevokedAtUtc.Value;
                await _db.SaveChangesAsync();
            }

            return MapLinkResponse(link, null);
        }

        // ---------------------------------------------------------------------
        // Recipient-facing (both sources)
        // ---------------------------------------------------------------------

        public async Task<ClubMemberInvitationResolveResponse> ResolveAsync(string token, int? userId)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("A token is required.");

            var tokenHash = ComputeTokenHash(token);
            var now = GetUtcNow();

            // 1. Specific invite (Redis).
            var invite = await GetInviteByHashAsync(tokenHash);
            if (invite != null)
                return await ResolveDirectInviteAsync(invite, userId, now);

            // 2. Shared link (DB).
            var link = await _db.ClubInvitationLinks
                .FirstOrDefaultAsync(l => l.TokenHash == tokenHash);
            if (link != null)
                return await ResolveLinkAsync(link, userId, now);

            return BuildResolve(ClubMemberInvitationResolveState.Invalid, ClubMemberInvitationSource.DirectInvite, null, null);
        }

        public async Task<ClubMemberInvitationDecisionResponse> AcceptAsync(string token, int userId)
        {
            var invite = await GetInviteByTokenAsync(token)
                ?? throw new ResourceNotFoundException("Invitation was not found or has expired.");

            if (invite.ExpiresAtUtc <= GetUtcNow())
            {
                await RemoveInviteAsync(invite.ClubId, invite);
                throw new GoneException("This invitation has expired.");
            }

            if (userId != invite.RecipientUserId)
                throw new ForbiddenException("This invitation does not belong to the current user.");

            await _clubService.GrantMembershipFromInvitationAsync(invite.ClubId, userId);
            await RemoveInviteAsync(invite.ClubId, invite);

            return new ClubMemberInvitationDecisionResponse
            {
                ClubId = invite.ClubId,
                Accepted = true
            };
        }

        public async Task<ClubMemberInvitationDecisionResponse> DeclineAsync(string token, int userId)
        {
            var invite = await GetInviteByTokenAsync(token)
                ?? throw new ResourceNotFoundException("Invitation was not found or has expired.");

            if (userId != invite.RecipientUserId)
                throw new ForbiddenException("This invitation does not belong to the current user.");

            await RemoveInviteAsync(invite.ClubId, invite);

            return new ClubMemberInvitationDecisionResponse
            {
                ClubId = invite.ClubId,
                Accepted = false
            };
        }

        public async Task<ClubMemberInvitationDecisionResponse> RedeemLinkAsync(string token, int userId)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("A token is required.");

            var tokenHash = ComputeTokenHash(token);
            var link = await _db.ClubInvitationLinks
                .FirstOrDefaultAsync(l => l.TokenHash == tokenHash)
                ?? throw new ResourceNotFoundException("Invitation link was not found.");

            var now = GetUtcNow();
            EnsureLinkCanBeRedeemed(link, now);

            var alreadyMember = await _followService.IsMemberAsync(link.ClubId, userId);
            await _clubService.GrantMembershipFromInvitationAsync(link.ClubId, userId);

            // Only count a genuine new join so a re-click never burns a redemption slot.
            if (!alreadyMember)
            {
                link.RedemptionCount += 1;
                link.UpdatedAt = now;
                await _db.SaveChangesAsync();
            }

            return new ClubMemberInvitationDecisionResponse
            {
                ClubId = link.ClubId,
                Accepted = true
            };
        }

        // ---------------------------------------------------------------------
        // Resolve helpers
        // ---------------------------------------------------------------------

        private async Task<ClubMemberInvitationResolveResponse> ResolveDirectInviteAsync(
            PendingClubMemberInvite invite,
            int? userId,
            DateTime now)
        {
            const ClubMemberInvitationSource source = ClubMemberInvitationSource.DirectInvite;

            var club = await TryGetClubSummaryAsync(invite.ClubId);
            if (club == null)
            {
                await RemoveInviteAsync(invite.ClubId, invite);
                return BuildResolve(ClubMemberInvitationResolveState.Invalid, source, null, null);
            }

            if (invite.ExpiresAtUtc <= now)
            {
                await RemoveInviteAsync(invite.ClubId, invite);
                return BuildResolve(ClubMemberInvitationResolveState.Expired, source, invite.ExpiresAtUtc, club);
            }

            if (!userId.HasValue)
                return BuildResolve(ClubMemberInvitationResolveState.LoginRequired, source, invite.ExpiresAtUtc, club);

            if (userId.Value != invite.RecipientUserId)
                return BuildResolve(ClubMemberInvitationResolveState.NotRecipient, source, invite.ExpiresAtUtc, club);

            if (await _followService.IsMemberAsync(invite.ClubId, userId.Value))
                return BuildResolve(ClubMemberInvitationResolveState.AlreadyMember, source, invite.ExpiresAtUtc, club);

            return BuildResolve(ClubMemberInvitationResolveState.AcceptAvailable, source, invite.ExpiresAtUtc, club);
        }

        private async Task<ClubMemberInvitationResolveResponse> ResolveLinkAsync(
            ClubInvitationLink link,
            int? userId,
            DateTime now)
        {
            const ClubMemberInvitationSource source = ClubMemberInvitationSource.Link;

            var club = await TryGetClubSummaryAsync(link.ClubId);
            if (club == null)
                return BuildResolve(ClubMemberInvitationResolveState.Invalid, source, null, null);

            if (link.RevokedAtUtc.HasValue)
                return BuildResolve(ClubMemberInvitationResolveState.Revoked, source, link.ExpiresAt, club);

            if (link.ExpiresAt <= now)
                return BuildResolve(ClubMemberInvitationResolveState.Expired, source, link.ExpiresAt, club);

            if (link.MaxRedemptions.HasValue && link.RedemptionCount >= link.MaxRedemptions.Value)
                return BuildResolve(ClubMemberInvitationResolveState.RedemptionsExhausted, source, link.ExpiresAt, club);

            if (!userId.HasValue)
                return BuildResolve(ClubMemberInvitationResolveState.LoginRequired, source, link.ExpiresAt, club);

            if (await _followService.IsMemberAsync(link.ClubId, userId.Value))
                return BuildResolve(ClubMemberInvitationResolveState.AlreadyMember, source, link.ExpiresAt, club);

            return BuildResolve(ClubMemberInvitationResolveState.AcceptAvailable, source, link.ExpiresAt, club);
        }

        private static void EnsureLinkCanBeRedeemed(ClubInvitationLink link, DateTime now)
        {
            if (link.RevokedAtUtc.HasValue)
                throw new GoneException("This invitation link has been revoked.");

            if (link.ExpiresAt <= now)
                throw new GoneException("This invitation link has expired.");

            if (link.MaxRedemptions.HasValue && link.RedemptionCount >= link.MaxRedemptions.Value)
                throw new GoneException("This invitation link has reached its limit.");
        }

        // ---------------------------------------------------------------------
        // Shared helpers
        // ---------------------------------------------------------------------

        private async Task<UserProfileRecord> ResolveRecipientAsync(string identifier)
        {
            var trimmed = identifier?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                throw new BadRequestException("A username or email address is required.");

            UserProfileRecord? user = LooksLikeEmail(trimmed)
                ? await _userRepository.GetProfileByEmailAsync(EmailPolicy.Normalize(trimmed))
                : await _userRepository.GetProfileByUsernameAsync(trimmed);

            return user
                ?? throw new ResourceNotFoundException($"No account found for '{trimmed}'.");
        }

        private static bool LooksLikeEmail(string value)
        {
            if (!value.Contains('@'))
                return false;

            try
            {
                _ = new MailAddress(value);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private async Task<PendingClubMemberInvite?> GetInviteByTokenAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("A token is required.");

            return await GetInviteByHashAsync(ComputeTokenHash(token));
        }

        private async Task<PendingClubMemberInvite?> GetInviteByHashAsync(string tokenHash)
        {
            var json = await _cache.GetValueAsync(TokenKey(tokenHash));
            return TryDeserialize(json, out var invite) ? invite : null;
        }

        private async Task RemoveInviteAsync(int clubId, PendingClubMemberInvite invite)
        {
            await _cache.DeleteKeyAsync(TokenKey(invite.TokenHash));
            await _cache.HashDeleteAsync(ClubIndexKey(clubId), invite.RecipientUserId.ToString());
        }

        private async Task EnsureCanManageAsync(int clubId, int actorUserId, string actorRole)
        {
            if (!await _clubService.CanManageClubAsync(clubId, actorUserId, actorRole))
                throw new ForbiddenException("Only club organisers can manage member invitations.");
        }

        private async Task<ClubMemberInvitationClubSummaryResponse?> TryGetClubSummaryAsync(int clubId)
        {
            try
            {
                var club = await _clubService.GetClub(clubId);
                return new ClubMemberInvitationClubSummaryResponse
                {
                    Id = club.Id,
                    Name = club.Name,
                    ClubImage = club.ClubImage
                };
            }
            catch (ResourceNotFoundException)
            {
                return null;
            }
        }

        private async Task QueueInvitationEmailAsync(string token, string clubName, UserProfileRecord user)
        {
            var message = new EmailMessage
            {
                Type = EmailMessageType.ClubMemberInvite,
                Email = user.Email,
                Token = token,
                RecipientName = string.IsNullOrWhiteSpace(user.Name) ? user.Username : user.Name,
                ClubName = clubName
            };

            await _publisher.PublishAsync(NotificationTopics.Email, message);
        }

        private static ClubMemberInvitationResolveResponse BuildResolve(
            ClubMemberInvitationResolveState state,
            ClubMemberInvitationSource source,
            DateTime? expiresAtUtc,
            ClubMemberInvitationClubSummaryResponse? club)
        {
            var canAccept = state == ClubMemberInvitationResolveState.AcceptAvailable;
            return new ClubMemberInvitationResolveResponse
            {
                State = state.ToString(),
                Source = source.ToString(),
                RequiresAuthentication = state == ClubMemberInvitationResolveState.LoginRequired,
                CanAccept = canAccept,
                // Only a specific invite can be declined; a shared link is simply ignored.
                CanDecline = canAccept && source == ClubMemberInvitationSource.DirectInvite,
                ExpiresAtUtc = expiresAtUtc,
                Club = club
            };
        }

        private static ClubMemberInvitationResponse MapResponse(PendingClubMemberInvite invite) => new()
        {
            ClubId = invite.ClubId,
            RecipientUserId = invite.RecipientUserId,
            RecipientEmail = invite.RecipientEmail,
            CreatedAtUtc = invite.CreatedAtUtc,
            ExpiresAtUtc = invite.ExpiresAtUtc
        };

        private static ClubInvitationLinkResponse MapLinkResponse(ClubInvitationLink link, string? rawToken) => new()
        {
            Id = link.Id,
            ClubId = link.ClubId,
            ShareUrl = rawToken == null
                ? null
                : $"/clubs/member-invite?token={Uri.EscapeDataString(rawToken)}",
            ExpiresAt = link.ExpiresAt,
            MaxRedemptions = link.MaxRedemptions,
            RedemptionCount = link.RedemptionCount,
            IsRevoked = link.RevokedAtUtc.HasValue,
            RevokedAtUtc = link.RevokedAtUtc,
            CreatedAt = link.CreatedAt,
            UpdatedAt = link.UpdatedAt
        };

        private static bool TryDeserialize(string? json, out PendingClubMemberInvite? invite)
        {
            invite = null;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                invite = JsonSerializer.Deserialize<PendingClubMemberInvite>(json);
                return invite != null;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private DateTime GetUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

        private static string GenerateOpaqueToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        private static string ComputeTokenHash(string token)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(hash);
        }
    }
}
