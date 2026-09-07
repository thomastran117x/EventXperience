using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using backend.main.features.cache;
using backend.main.features.clubs.invitations.contracts.responses;
using backend.main.features.clubs.staff;
using backend.main.features.profile;
using backend.main.features.profile.contracts;
using backend.main.shared.exceptions.http;
using backend.main.shared.providers;
using backend.main.shared.providers.messages;

namespace backend.main.features.clubs.invitations
{
    public sealed class ClubInvitationService : IClubInvitationService
    {
        private readonly ICacheService _cache;
        private readonly IClubService _clubService;
        private readonly IUserRepository _userRepository;
        private readonly IPublisher _publisher;
        private readonly TimeProvider _timeProvider;

        private static readonly TimeSpan InvitationTtl = TimeSpan.FromDays(14);

        public ClubInvitationService(
            ICacheService cache,
            IClubService clubService,
            IUserRepository userRepository,
            IPublisher publisher,
            TimeProvider timeProvider)
        {
            _cache = cache;
            _clubService = clubService;
            _userRepository = userRepository;
            _publisher = publisher;
            _timeProvider = timeProvider;
        }

        private static string TokenKey(string tokenHash) => $"club:invite:token:{tokenHash}";

        private static string ClubIndexKey(int clubId) => $"club:invite:club:{clubId}";

        public async Task<ClubInvitationResponse> CreateInvitationAsync(
            int clubId,
            int actorUserId,
            string actorRole,
            string identifier,
            ClubStaffRole role)
        {
            var club = await _clubService.GetClub(clubId);
            await EnsureOwnerAsync(clubId, actorUserId, actorRole);

            var user = await ResolveRecipientAsync(identifier);

            if (user.Id == club.UserId)
                throw new ConflictException("The club owner already has full access.");

            if (await _clubService.IsClubStaffMemberAsync(clubId, user.Id))
                throw new ConflictException("User already has a staff role for this club.");

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
            var invite = new PendingClubInvite
            {
                ClubId = clubId,
                RecipientUserId = user.Id,
                RecipientEmail = user.Email,
                Role = role,
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

            await QueueInvitationEmailAsync(invite, token, club.Name, user);

            return MapResponse(invite);
        }

        public async Task<IReadOnlyList<ClubInvitationResponse>> GetClubInvitationsAsync(int clubId, int actorUserId, string actorRole)
        {
            _ = await _clubService.GetClub(clubId);
            await EnsureOwnerAsync(clubId, actorUserId, actorRole);

            var now = GetUtcNow();
            var entries = await _cache.HashGetAllAsync(ClubIndexKey(clubId));
            var results = new List<ClubInvitationResponse>();

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
            await EnsureOwnerAsync(clubId, actorUserId, actorRole);

            var json = await _cache.HashGetAsync(ClubIndexKey(clubId), recipientUserId.ToString());
            if (!TryDeserialize(json, out var invite))
                throw new ResourceNotFoundException("Invitation was not found.");

            await RemoveInviteAsync(clubId, invite!);
        }

        public async Task<ClubInvitationResolveResponse> ResolveInvitationAsync(string token, int? userId)
        {
            var invite = await GetInviteByTokenAsync(token);
            if (invite == null)
                return BuildResolveResponse(ClubInvitationResolveState.Invalid, null, null);

            var club = await TryGetClubSummaryAsync(invite.ClubId);
            if (club == null)
            {
                await RemoveInviteAsync(invite.ClubId, invite);
                return BuildResolveResponse(ClubInvitationResolveState.Invalid, invite, null);
            }

            if (invite.ExpiresAtUtc <= GetUtcNow())
            {
                await RemoveInviteAsync(invite.ClubId, invite);
                return BuildResolveResponse(ClubInvitationResolveState.Expired, invite, club);
            }

            if (!userId.HasValue)
                return BuildResolveResponse(ClubInvitationResolveState.LoginRequired, invite, club);

            if (userId.Value != invite.RecipientUserId)
                return BuildResolveResponse(ClubInvitationResolveState.NotRecipient, invite, club);

            return BuildResolveResponse(ClubInvitationResolveState.AcceptAvailable, invite, club);
        }

        public async Task<ClubInvitationDecisionResponse> AcceptInvitationAsync(string token, int userId, string userEmail)
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

            await _clubService.GrantStaffFromInvitationAsync(invite.ClubId, userId, invite.Role, invite.CreatedByUserId);
            await RemoveInviteAsync(invite.ClubId, invite);

            return new ClubInvitationDecisionResponse
            {
                ClubId = invite.ClubId,
                Role = invite.Role.ToString(),
                Accepted = true
            };
        }

        public async Task<ClubInvitationDecisionResponse> DeclineInvitationAsync(string token, int userId)
        {
            var invite = await GetInviteByTokenAsync(token)
                ?? throw new ResourceNotFoundException("Invitation was not found or has expired.");

            if (userId != invite.RecipientUserId)
                throw new ForbiddenException("This invitation does not belong to the current user.");

            await RemoveInviteAsync(invite.ClubId, invite);

            return new ClubInvitationDecisionResponse
            {
                ClubId = invite.ClubId,
                Role = invite.Role.ToString(),
                Accepted = false
            };
        }

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

        private async Task<PendingClubInvite?> GetInviteByTokenAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("A token is required.");

            var json = await _cache.GetValueAsync(TokenKey(ComputeTokenHash(token)));
            return TryDeserialize(json, out var invite) ? invite : null;
        }

        private async Task RemoveInviteAsync(int clubId, PendingClubInvite invite)
        {
            await _cache.DeleteKeyAsync(TokenKey(invite.TokenHash));
            await _cache.HashDeleteAsync(ClubIndexKey(clubId), invite.RecipientUserId.ToString());
        }

        private async Task EnsureOwnerAsync(int clubId, int actorUserId, string actorRole)
        {
            if (!await _clubService.IsClubOwnerAsync(clubId, actorUserId, actorRole))
                throw new ForbiddenException("Only the club owner can manage staff invitations.");
        }

        private async Task<ClubInvitationClubSummaryResponse?> TryGetClubSummaryAsync(int clubId)
        {
            try
            {
                var club = await _clubService.GetClub(clubId);
                return new ClubInvitationClubSummaryResponse
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

        private async Task QueueInvitationEmailAsync(PendingClubInvite invite, string token, string clubName, UserProfileRecord user)
        {
            var message = new EmailMessage
            {
                Type = EmailMessageType.ClubStaffInvite,
                Email = user.Email,
                Token = token,
                RecipientName = string.IsNullOrWhiteSpace(user.Name) ? user.Username : user.Name,
                ClubName = clubName
            };

            await _publisher.PublishAsync(NotificationTopics.Email, message);
        }

        private static ClubInvitationResolveResponse BuildResolveResponse(
            ClubInvitationResolveState state,
            PendingClubInvite? invite,
            ClubInvitationClubSummaryResponse? club)
        {
            return new ClubInvitationResolveResponse
            {
                State = state.ToString(),
                RequiresAuthentication = state == ClubInvitationResolveState.LoginRequired,
                CanAccept = state == ClubInvitationResolveState.AcceptAvailable,
                CanDecline = state == ClubInvitationResolveState.AcceptAvailable,
                Role = invite?.Role.ToString(),
                ExpiresAtUtc = invite?.ExpiresAtUtc,
                Club = club
            };
        }

        private static ClubInvitationResponse MapResponse(PendingClubInvite invite) => new()
        {
            ClubId = invite.ClubId,
            RecipientUserId = invite.RecipientUserId,
            RecipientEmail = invite.RecipientEmail,
            Role = invite.Role.ToString(),
            CreatedAtUtc = invite.CreatedAtUtc,
            ExpiresAtUtc = invite.ExpiresAtUtc
        };

        private static bool TryDeserialize(string? json, out PendingClubInvite? invite)
        {
            invite = null;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                invite = JsonSerializer.Deserialize<PendingClubInvite>(json);
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
