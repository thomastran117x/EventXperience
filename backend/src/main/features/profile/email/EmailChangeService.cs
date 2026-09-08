using backend.main.features.auth;
using backend.main.features.auth.notifications;
using backend.main.features.auth.token;
using backend.main.features.cache;
using backend.main.features.events.invitations;
using backend.main.features.profile.contracts;
using backend.main.seeders;
using backend.main.shared.exceptions.http;
using backend.main.shared.utilities.logger;

namespace backend.main.features.profile.email
{
    public class EmailChangeService : IEmailChangeService
    {
        private readonly IAuthUserRepository _userRepository;
        private readonly ITokenService _tokenService;
        private readonly IEmailAvailabilityService _emailAvailability;
        private readonly IAuthNotificationService _notificationService;
        private readonly IEventInvitationService _invitationService;
        private readonly IRefreshAheadCache _refreshCache;
        private readonly TimeProvider _timeProvider;

        private static string GetUserCacheKey(int userId) => $"user:{userId}";

        public EmailChangeService(
            IAuthUserRepository userRepository,
            ITokenService tokenService,
            IEmailAvailabilityService emailAvailability,
            IAuthNotificationService notificationService,
            IEventInvitationService invitationService,
            IRefreshAheadCache refreshCache,
            TimeProvider timeProvider
        )
        {
            _userRepository = userRepository;
            _tokenService = tokenService;
            _emailAvailability = emailAvailability;
            _notificationService = notificationService;
            _invitationService = invitationService;
            _refreshCache = refreshCache;
            _timeProvider = timeProvider;
        }

        public async Task<VerificationOtpChallenge> RequestChangeAsync(
            int userId,
            string newEmail,
            string? currentPassword,
            CancellationToken cancellationToken = default
        )
        {
            var sanitizedEmail = EmailPolicy.Sanitize(newEmail);
            var normalizedEmail = EmailPolicy.NormalizeAndValidate(newEmail);

            // The auth-shaped lookup, because GetUserAsync sanitizes the password away and this
            // needs to re-verify it. Keyed by id, not address: the address is the thing changing.
            var user = await _userRepository.GetAuthByIdAsync(userId)
                ?? throw new ResourceNotFoundException($"User with the id {userId} is not found");

            if (user.IsDisabled)
                throw new ForbiddenException("This account is disabled.");

            if (EmailPolicy.Normalize(user.Email) == normalizedEmail)
                throw new BadRequestException(
                    "New email must be different from your current email.");

            // SeedAccountBypassPolicy grants captcha and MFA bypasses to anything under the seed
            // domain by suffix match. A verified change into that domain would be a way to hand
            // yourself one, so the domain is closed off here rather than trusted to stay dev-only.
            if (sanitizedEmail.EndsWith(
                    SeedCatalogConstants.SeedEmailDomain,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new BadRequestException("This email domain is reserved.");
            }

            // An OAuth-only account has no password to prove, and MFA step-up has already gated
            // the request by the time we get here.
            if (user.Password != null)
            {
                if (string.IsNullOrEmpty(currentPassword))
                    throw new BadRequestException(
                        "Your current password is required to change your email.");

                if (!VerifyPassword(currentPassword, user.Password))
                    throw new UnauthorizedException("Current password is incorrect.");
            }

            if (await _emailAvailability.IsRegisteredAsync(
                    normalizedEmail,
                    AvailabilityLookupMode.Authoritative,
                    cancellationToken))
            {
                throw new ConflictException("That email is already in use.");
            }

            var artifacts = await _tokenService.GenerateEmailChangeArtifactsAsync(
                userId,
                user.AuthVersion,
                sanitizedEmail);

            await _notificationService.SendEmailChangeVerificationAsync(
                sanitizedEmail,
                artifacts.LinkToken,
                artifacts.OtpChallenge.Code,
                user.Name);

            // Best effort: the old inbox losing its heads-up must not fail a request whose
            // confirmation has already been sent.
            try
            {
                await _notificationService.SendEmailChangeRequestedAsync(
                    user.Email,
                    sanitizedEmail,
                    user.Name);
            }
            catch (Exception e)
            {
                Logger.Warn(
                    $"[EmailChangeService] Notifying {userId}'s previous address failed: {e}");
            }

            return artifacts.OtpChallenge;
        }

        public async Task ConfirmAsync(
            PendingEmailChange pending,
            CancellationToken cancellationToken = default
        )
        {
            var sanitizedEmail = EmailPolicy.Sanitize(pending.NewEmail);
            var normalizedEmail = EmailPolicy.Normalize(sanitizedEmail);

            var user = await _userRepository.GetAuthByIdAsync(pending.UserId)
                ?? throw new UnauthorizedException("This email change is no longer valid.");

            if (user.IsDisabled)
                throw new ForbiddenException("This account is disabled.");

            // A pending change must not survive a credential rotation. The heads-up sent to the
            // old address tells its owner to change their password if they did not ask for this,
            // so that has to actually revoke the proof - otherwise the advice is worthless and a
            // stolen session's request stays redeemable for the rest of its TTL.
            if (user.AuthVersion != pending.AuthVersion)
            {
                await _tokenService.CancelPendingEmailChangeAsync(pending.UserId);
                throw new UnauthorizedException(
                    "This email change is no longer valid because the account's credentials changed."
                );
            }

            // Re-checked rather than trusted from request time: the address was free 30 minutes
            // ago, which says nothing about now.
            if (await _emailAvailability.IsRegisteredAsync(
                    normalizedEmail,
                    AvailabilityLookupMode.Authoritative,
                    cancellationToken))
            {
                throw new ConflictException("That email is already in use.");
            }

            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            var result = await _userRepository.ChangeEmailAsync(
                pending.UserId,
                sanitizedEmail,
                utcNow);

            switch (result.Status)
            {
                case EmailChangeStatus.Changed when result.User != null:
                    break;
                case EmailChangeStatus.UserNotFound:
                    throw new UnauthorizedException("This email change is no longer valid.");
                case EmailChangeStatus.Unchanged:
                    // Already on the target address, so the change has effectively been applied.
                    return;
                case EmailChangeStatus.Unavailable:
                    throw new ConflictException("That email is already in use.");
                default:
                    throw new InternalServerErrorException();
            }

            var previousEmail = result.PreviousEmail ?? user.Email;
            var previousNormalizedEmail = EmailPolicy.Normalize(previousEmail);

            // Only the new address is recorded. The old one stays set in the filter until the next
            // scheduled rebuild sheds it, which is safe: a bloom filter has no delete, and a false
            // positive only costs the authoritative database lookup that every write already makes.
            await _emailAvailability.MarkRegisteredAsync(normalizedEmail, cancellationToken);

            // The address is an access token claim, so every outstanding session has to go. The
            // AuthVersion bump inside ChangeEmailAsync already invalidates the access tokens;
            // this drops the refresh sessions that would otherwise mint new ones.
            await _tokenService.RevokeAllRefreshSessionsAsync(pending.UserId);
            await _refreshCache.RemoveAsync(GetUserCacheKey(pending.UserId));

            await _invitationService.RelinkForEmailChangeAsync(
                pending.UserId,
                previousNormalizedEmail,
                normalizedEmail);

            // Best effort, and after the change has committed: a mail failure must not roll back
            // an address the user has already proved and been signed out for.
            try
            {
                await _notificationService.SendEmailChangedAsync(
                    previousEmail,
                    sanitizedEmail,
                    user.Name);
                await _notificationService.SendEmailChangedAsync(
                    sanitizedEmail,
                    sanitizedEmail,
                    user.Name);
            }
            catch (Exception e)
            {
                Logger.Warn(
                    $"[EmailChangeService] Notifying {pending.UserId} of the applied change failed: {e}");
            }
        }

        public Task<PendingEmailChange?> GetPendingAsync(int userId) =>
            _tokenService.GetPendingEmailChangeAsync(userId);

        public Task CancelPendingAsync(int userId) =>
            _tokenService.CancelPendingEmailChangeAsync(userId);

        private bool VerifyPassword(string password, string hashedPassword)
        {
            try
            {
                return BCrypt.Net.BCrypt.Verify(password, hashedPassword);
            }
            catch (Exception e)
            {
                Logger.Error($"[EmailChangeService] VerifyPassword failed: {e}");
                throw new InternalServerErrorException();
            }
        }
    }
}
