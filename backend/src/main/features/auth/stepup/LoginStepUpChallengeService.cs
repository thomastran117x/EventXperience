using System.Security.Cryptography;
using System.Text;

using backend.main.application.environment;
using backend.main.features.auth.contracts.responses;
using backend.main.features.auth.device;
using backend.main.features.auth.mfa;
using backend.main.features.auth.mfa.totp;
using backend.main.features.auth.notifications;
using backend.main.features.auth.token;
using backend.main.features.cache;
using backend.main.features.profile;
using backend.main.shared.exceptions.http;
using backend.main.shared.requests;
using backend.main.shared.utilities;
using backend.main.shared.utilities.logger;

using Microsoft.IdentityModel.Tokens;

using Newtonsoft.Json;

namespace backend.main.features.auth.stepup
{
    public sealed class LoginStepUpChallengeService : ILoginStepUpChallengeService
    {
        private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan CompletionLockTtl = TimeSpan.FromSeconds(30);
        private const int MaxSmsAttempts = 5;
        private const int MaxTotpAttempts = 5;
        private const int MaxMethodSends = 3;
        private const int MaxUserSends = 6;
        private const int MaxIpSends = 20;
        private const string EmailMethod = "email";
        private const string SmsMethod = "sms";
        private const string TotpMethod = "totp";

        private readonly ICacheService _cacheService;
        private readonly IAuthNotificationService _notificationService;
        private readonly IMfaEnrollmentRepository _mfaEnrollmentRepository;
        private readonly ITotpMfaEnrollmentService _totpMfaEnrollmentService;
        private readonly IDeviceTrustService _deviceTrustService;
        private readonly IAuthUserRepository _userRepository;
        private readonly IAuthSessionService _authSessionService;
        private readonly ITokenService _tokenService;
        private readonly ClientRequestInfo _requestInfo;

        public LoginStepUpChallengeService(
            ICacheService cacheService,
            IAuthNotificationService notificationService,
            IMfaEnrollmentRepository mfaEnrollmentRepository,
            ITotpMfaEnrollmentService totpMfaEnrollmentService,
            IDeviceTrustService deviceTrustService,
            IAuthUserRepository userRepository,
            IAuthSessionService authSessionService,
            ITokenService tokenService,
            ClientRequestInfo requestInfo
        )
        {
            _cacheService = cacheService;
            _notificationService = notificationService;
            _mfaEnrollmentRepository = mfaEnrollmentRepository;
            _totpMfaEnrollmentService = totpMfaEnrollmentService;
            _deviceTrustService = deviceTrustService;
            _userRepository = userRepository;
            _authSessionService = authSessionService;
            _tokenService = tokenService;
            _requestInfo = requestInfo;
        }

        public async Task<LoginStepUpChallengeResponse> CreateChallengeAsync(
            User user,
            SessionTransport transport,
            bool rememberMe,
            string? returnUrl
        )
        {
            try
            {
                var smsEnrollment = await _mfaEnrollmentRepository.GetByUserIdAsync(user.Id);
                var totpEnrollment = await _totpMfaEnrollmentService.GetEnrollmentAsync(user.Id);
                var rawChallenge = CreateRandomToken();
                var state = new PendingStepUpState
                {
                    PendingId = CreateRandomToken(),
                    ChallengeHash = CryptoHelper.HashToken(rawChallenge),
                    UserId = user.Id,
                    AuthVersion = user.AuthVersion,
                    Email = user.Email,
                    PhoneNumber = CanUseSms(smsEnrollment) ? smsEnrollment!.PhoneNumber : null,
                    HasTotp = totpEnrollment?.IsTotpMfaEnabled == true && EnvironmentSetting.AuthTotpMfaStepUpEnabled,
                    Transport = transport,
                    RememberMe = rememberMe,
                    TrustedDeviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    DeviceType = _requestInfo.DeviceType,
                    ClientName = _requestInfo.ClientName,
                    IpAddress = _requestInfo.IpAddress,
                    ReturnPath = NormalizeReturnPath(returnUrl),
                    ExpiresAtUtc = DateTime.UtcNow.Add(ChallengeTtl),
                    Sms = new DeliveryState(),
                    EmailDelivery = new DeliveryState()
                };

                if (!await PersistStateAsync(state))
                    throw new NotAvailableException();

                return ToChallengeResponse(rawChallenge, state);
            }
            catch (Exception ex)
            {
                if (ex is AppException)
                    throw;

                Logger.Error($"[LoginStepUpChallengeService] CreateChallengeAsync failed: {ex}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<StartLoginStepUpResponse> StartAsync(string challenge, string method)
        {
            try
            {
                var normalizedMethod = NormalizeMethod(method);
                var challengeHash = CryptoHelper.HashToken(challenge);
                var state = await GetStateByHashAsync(challengeHash)
                    ?? throw new UnauthorizedException("Invalid or expired sign-in verification challenge.");

                EnsureMethodAllowed(state, normalizedMethod);
                var now = DateTime.UtcNow;

                // TOTP requires no delivery â€” return immediately with the existing challenge.
                if (normalizedMethod == TotpMethod)
                {
                    return new StartLoginStepUpResponse
                    {
                        Challenge = challenge,
                        ExpiresAtUtc = state.ExpiresAtUtc,
                        SelectedMethod = TotpMethod,
                        MaskedDestination = "authenticator app",
                        CooldownEndsAtUtc = DateTime.MinValue,
                        AvailableMethods = GetAvailableMethods(state),
                        MaskedPhone = CanUseSms(state) ? PhoneNumberFormatter.Mask(state.PhoneNumber!) : null,
                        MaskedEmail = PhoneNumberFormatter.MaskEmail(state.Email)
                    };
                }

                EnforceCooldown(state, normalizedMethod, now);
                await EnforceSendCapsAsync(state);

                var previousChallengeHash = state.ChallengeHash;
                var previousEmailTokenHash = state.EmailTokenHash;
                var nextChallenge = CreateRandomToken();
                state.ChallengeHash = CryptoHelper.HashToken(nextChallenge);
                state.SmsCodeHash = null;
                state.EmailTokenHash = null;
                state.SmsFailedAttempts = 0;

                var delivery = GetDeliveryState(state, normalizedMethod)!;
                delivery.SendCount += 1;
                delivery.CooldownEndsAtUtc = now.Add(ResendCooldown);

                string maskedDestination;
                string? smsCode = null;
                string? emailToken = null;

                if (normalizedMethod == SmsMethod)
                {
                    smsCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                    state.SmsCodeHash = HashCode(state.PendingId, state.ChallengeHash, smsCode);
                    maskedDestination = PhoneNumberFormatter.Mask(state.PhoneNumber!);
                }
                else
                {
                    emailToken = CreateRandomToken();
                    state.EmailTokenHash = CryptoHelper.HashToken(emailToken);
                    maskedDestination = PhoneNumberFormatter.MaskEmail(state.Email);
                }

                if (!await PersistStateAsync(state, previousChallengeHash, previousEmailTokenHash))
                    throw new NotAvailableException();

                try
                {
                    if (normalizedMethod == SmsMethod)
                    {
                        await _notificationService.SendSmsMfaAsync(
                            state.PhoneNumber!,
                            smsCode!,
                            nextChallenge,
                            state.ExpiresAtUtc,
                            "sign-in verification"
                        );
                    }
                    else
                    {
                        await _notificationService.SendDeviceVerificationAsync(state.Email, emailToken!);
                    }
                }
                catch
                {
                    // Restore previous challenge state so user can retry without a full re-login,
                    // and refund the send-cap counters consumed for this failed attempt.
                    var rotatedChallengeHash = state.ChallengeHash;
                    var rotatedEmailTokenHash = state.EmailTokenHash;
                    state.ChallengeHash = previousChallengeHash;
                    state.SmsCodeHash = null;
                    state.EmailTokenHash = previousEmailTokenHash;
                    state.SmsFailedAttempts = 0;
                    delivery.SendCount -= 1;
                    delivery.CooldownEndsAtUtc = null;
                    await _cacheService.DecrementAsync(UserSendKey(state.UserId));
                    await _cacheService.DecrementAsync(IpSendKey(_requestInfo.IpAddress));
                    await PersistStateAsync(state, rotatedChallengeHash, rotatedEmailTokenHash);
                    throw;
                }

                return new StartLoginStepUpResponse
                {
                    Challenge = nextChallenge,
                    ExpiresAtUtc = state.ExpiresAtUtc,
                    SelectedMethod = normalizedMethod,
                    MaskedDestination = maskedDestination,
                    CooldownEndsAtUtc = delivery.CooldownEndsAtUtc ?? now.Add(ResendCooldown),
                    AvailableMethods = GetAvailableMethods(state),
                    MaskedPhone = CanUseSms(state) ? PhoneNumberFormatter.Mask(state.PhoneNumber!) : null,
                    MaskedEmail = PhoneNumberFormatter.MaskEmail(state.Email)
                };
            }
            catch (Exception ex)
            {
                if (ex is AppException)
                    throw;

                Logger.Error($"[LoginStepUpChallengeService] StartAsync failed: {ex}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<AuthenticatedSessionResult> VerifySmsAsync(string challenge, string code)
        {
            try
            {
                var challengeHash = CryptoHelper.HashToken(challenge);
                var state = await GetStateByHashAsync(challengeHash)
                    ?? throw new UnauthorizedException("Invalid or expired sign-in verification challenge.");

                if (string.IsNullOrWhiteSpace(state.SmsCodeHash))
                    throw new UnauthorizedException("Invalid or expired sign-in verification code.");

                var expectedHash = HashCode(state.PendingId, state.ChallengeHash, code);
                if (!CryptoHelper.FixedTimeEquals(state.SmsCodeHash, expectedHash))
                {
                    state.SmsFailedAttempts += 1;
                    if (state.SmsFailedAttempts >= MaxSmsAttempts)
                    {
                        await DeleteStateAsync(state);
                    }
                    else if (!await PersistStateAsync(state, challengeHash, state.EmailTokenHash))
                    {
                        throw new NotAvailableException();
                    }

                    throw new UnauthorizedException("Invalid or expired sign-in verification code.");
                }

                return await CompletePendingChallengeAsync(state);
            }
            catch (Exception ex)
            {
                if (ex is AppException)
                    throw;

                Logger.Error($"[LoginStepUpChallengeService] VerifySmsAsync failed: {ex}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<AuthenticatedSessionResult> VerifyTotpAsync(string challenge, string code)
        {
            try
            {
                var challengeHash = CryptoHelper.HashToken(challenge);
                var state = await GetStateByHashAsync(challengeHash)
                    ?? throw new UnauthorizedException("Invalid or expired sign-in verification challenge.");

                if (!state.HasTotp)
                    throw new UnauthorizedException("TOTP MFA is not available for this sign-in challenge.");

                try
                {
                    await _totpMfaEnrollmentService.VerifyPersistedCodeAsync(state.UserId, code);
                }
                catch (UnauthorizedException)
                {
                    state.TotpFailedAttempts += 1;
                    if (state.TotpFailedAttempts >= MaxTotpAttempts)
                    {
                        await DeleteStateAsync(state);
                    }
                    else if (!await PersistStateAsync(state, challengeHash, state.EmailTokenHash))
                    {
                        throw new NotAvailableException();
                    }

                    throw;
                }

                return await CompletePendingChallengeAsync(state);
            }
            catch (Exception ex)
            {
                if (ex is AppException)
                    throw;

                Logger.Error($"[LoginStepUpChallengeService] VerifyTotpAsync failed: {ex}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<AuthenticatedSessionResult?> TryVerifyEmailAsync(string token)
        {
            try
            {
                var tokenHash = CryptoHelper.HashToken(token);
                var json = await _cacheService.GetValueAsync(EmailTokenKey(tokenHash));
                if (string.IsNullOrWhiteSpace(json))
                    return null;

                var state = JsonConvert.DeserializeObject<PendingStepUpState>(json)
                    ?? throw new UnauthorizedException("Invalid or expired device verification token.");

                if (!string.Equals(state.EmailTokenHash, tokenHash, StringComparison.Ordinal))
                    throw new UnauthorizedException("Invalid or expired device verification token.");

                return await CompletePendingChallengeAsync(state);
            }
            catch (Exception ex)
            {
                if (ex is AppException)
                    throw;

                Logger.Error($"[LoginStepUpChallengeService] TryVerifyEmailAsync failed: {ex}");
                throw new InternalServerErrorException();
            }
        }

        private async Task<AuthenticatedSessionResult> CompletePendingChallengeAsync(PendingStepUpState state)
        {
            var lockValue = CreateRandomToken();
            var lockKey = CompletionLockKey(state.PendingId);
            if (!await _cacheService.AcquireLockAsync(lockKey, lockValue, CompletionLockTtl))
                throw new UnauthorizedException("This sign-in verification challenge has already been used.");

            try
            {
                var user = await _userRepository.GetUserAsync(state.UserId)
                    ?? throw new ResourceNotFoundException($"User with ID {state.UserId} not found.");
                if (user.IsDisabled)
                    throw new ForbiddenException("This account is disabled.");

                // Revoking sessions cannot reach a challenge that is mid-flight: it holds no
                // session yet. Without this check, a password or email change that signed every
                // device out would still be followed by a challenge completing into a fresh
                // session for whoever started it.
                if (user.AuthVersion != state.AuthVersion)
                {
                    await DeleteStateAsync(state);
                    throw new UnauthorizedException(
                        "This sign-in verification challenge is no longer valid. Please sign in again."
                    );
                }

                var userToken = await _authSessionService.IssueAsync(
                    user,
                    state.Transport,
                    rememberMe: state.RememberMe
                );

                // The check above is not enough on its own. A rotation committing between it and
                // the line above would run its revocation before this session existed, so nothing
                // would have removed it - and although the access token just minted carries the
                // stale version and is rejected, the refresh session would still mint valid ones.
                // Re-read once the session exists and undo it if the account moved on.
                var current = await _userRepository.GetUserAsync(state.UserId);
                if (current == null || current.AuthVersion != state.AuthVersion)
                {
                    // Revoking the account's sessions rather than just this one: a rotation has
                    // happened, so everything is meant to be gone, and this is idempotent with the
                    // revocation the rotation already ran.
                    await _tokenService.RevokeAllRefreshSessionsAsync(state.UserId);
                    await DeleteStateAsync(state);
                    throw new UnauthorizedException(
                        "This sign-in verification challenge is no longer valid. Please sign in again."
                    );
                }

                // Deliberately after the re-check rather than before the session is issued.
                // Trusting a device persists for 90 days and lets it skip MFA, and an email change
                // leaves the password intact - so granting trust on a path that then rejects the
                // sign-in would hand a lasting bypass to whoever completed the stale challenge.
                await _deviceTrustService.TrustAsync(
                    state.UserId,
                    state.TrustedDeviceId,
                    state.DeviceType,
                    state.ClientName,
                    state.IpAddress
                );

                await DeleteStateAsync(state);

                return new AuthenticatedSessionResult
                {
                    UserToken = userToken,
                    ReturnPath = state.ReturnPath
                };
            }
            finally
            {
                await _cacheService.ReleaseLockAsync(lockKey, lockValue);
            }
        }

        private async Task EnforceSendCapsAsync(PendingStepUpState state)
        {
            await IncrementWithLimitAsync(
                UserSendKey(state.UserId),
                MaxUserSends,
                "Too many sign-in verification deliveries have been requested for this account. Please try again later."
            );
            await IncrementWithLimitAsync(
                IpSendKey(_requestInfo.IpAddress),
                MaxIpSends,
                "Too many sign-in verification deliveries have been requested from this IP. Please try again later."
            );
        }

        private async Task IncrementWithLimitAsync(string key, int limit, string message)
        {
            var count = await _cacheService.IncrementAsync(key);
            if (count == 1)
                await _cacheService.SetExpiryAsync(key, ChallengeTtl);

            if (count > limit)
                throw new TooManyRequestException(message);
        }

        private static void EnforceCooldown(PendingStepUpState state, string method, DateTime now)
        {
            var delivery = GetDeliveryState(state, method);
            if (delivery == null)
                return;

            if (delivery.SendCount >= MaxMethodSends)
                throw new TooManyRequestException($"Too many {method} verification deliveries have been requested for this sign-in.");

            if (delivery.CooldownEndsAtUtc.HasValue && delivery.CooldownEndsAtUtc.Value > now)
            {
                var seconds = Math.Max(1, (int)Math.Ceiling((delivery.CooldownEndsAtUtc.Value - now).TotalSeconds));
                throw new TooManyRequestException($"Please wait {seconds} seconds before requesting another {method} verification.");
            }
        }

        private static DeliveryState? GetDeliveryState(PendingStepUpState state, string method) =>
            method switch
            {
                SmsMethod => state.Sms,
                EmailMethod => state.EmailDelivery,
                _ => null,
            };

        private static void EnsureMethodAllowed(PendingStepUpState state, string method)
        {
            var availableMethods = GetAvailableMethods(state);
            if (!availableMethods.Contains(method, StringComparer.Ordinal))
                throw new BadRequestException("The requested sign-in verification method is unavailable.");
        }

        private async Task<PendingStepUpState?> GetStateByHashAsync(string challengeHash)
        {
            var json = await _cacheService.GetValueAsync(ChallengeKey(challengeHash));
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonConvert.DeserializeObject<PendingStepUpState>(json);
        }

        private async Task<bool> PersistStateAsync(
            PendingStepUpState state,
            string? previousChallengeHash = null,
            string? previousEmailTokenHash = null
        )
        {
            var ttl = state.ExpiresAtUtc - DateTime.UtcNow;
            if (ttl <= TimeSpan.Zero)
                return false;

            var json = JsonConvert.SerializeObject(state);
            if (!string.IsNullOrWhiteSpace(previousChallengeHash) && previousChallengeHash != state.ChallengeHash)
                await _cacheService.DeleteKeyAsync(ChallengeKey(previousChallengeHash));
            if (!string.IsNullOrWhiteSpace(previousEmailTokenHash) && previousEmailTokenHash != state.EmailTokenHash)
                await _cacheService.DeleteKeyAsync(EmailTokenKey(previousEmailTokenHash));

            var challengeStored = await _cacheService.SetValueAsync(ChallengeKey(state.ChallengeHash), json, ttl);
            if (!challengeStored)
                return false;

            if (!string.IsNullOrWhiteSpace(state.EmailTokenHash))
            {
                var emailStored = await _cacheService.SetValueAsync(EmailTokenKey(state.EmailTokenHash), json, ttl);
                if (!emailStored)
                {
                    await _cacheService.DeleteKeyAsync(ChallengeKey(state.ChallengeHash));
                    return false;
                }
            }

            return true;
        }

        private async Task DeleteStateAsync(PendingStepUpState state)
        {
            await _cacheService.DeleteKeyAsync(ChallengeKey(state.ChallengeHash));
            if (!string.IsNullOrWhiteSpace(state.EmailTokenHash))
                await _cacheService.DeleteKeyAsync(EmailTokenKey(state.EmailTokenHash));
        }

        private static LoginStepUpChallengeResponse ToChallengeResponse(string rawChallenge, PendingStepUpState state)
        {
            return new LoginStepUpChallengeResponse
            {
                Challenge = rawChallenge,
                ExpiresAtUtc = state.ExpiresAtUtc,
                AvailableMethods = GetAvailableMethods(state),
                MaskedPhone = CanUseSms(state) ? PhoneNumberFormatter.Mask(state.PhoneNumber!) : null,
                MaskedEmail = PhoneNumberFormatter.MaskEmail(state.Email)
            };
        }

        private static string[] GetAvailableMethods(PendingStepUpState state)
        {
            var methods = new List<string>();
            if (state.HasTotp)
                methods.Add(TotpMethod);
            if (CanUseSms(state))
                methods.Add(SmsMethod);
            methods.Add(EmailMethod);
            return [.. methods];
        }

        private static bool CanUseSms(SmsMfaEnrollment? enrollment)
        {
            if (enrollment == null || !enrollment.IsSmsMfaEnabled || string.IsNullOrWhiteSpace(enrollment.PhoneNumber))
                return false;

            return EnvironmentSetting.AuthSmsMfaStepUpSmsEnabled &&
                (!string.IsNullOrWhiteSpace(EnvironmentSetting.TwilioAccountSid)
                    || EnvironmentSetting.AppEnvironment is "development" or "test" or "testing");
        }

        private static bool CanUseSms(PendingStepUpState state) => !string.IsNullOrWhiteSpace(state.PhoneNumber);

        private static string NormalizeMethod(string method)
        {
            var normalized = method?.Trim().ToLowerInvariant();
            return normalized switch
            {
                EmailMethod => EmailMethod,
                SmsMethod => SmsMethod,
                TotpMethod => TotpMethod,
                _ => throw new BadRequestException("Unsupported sign-in verification method.")
            };
        }

        private static string NormalizeReturnPath(string? returnUrl)
        {
            if (string.IsNullOrWhiteSpace(returnUrl))
                return "/dashboard";

            var trimmed = returnUrl.Trim();
            if (!trimmed.StartsWith("/", StringComparison.Ordinal)
                || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("/\\", StringComparison.Ordinal))
                return "/dashboard";

            return trimmed;
        }

        private static string CreateRandomToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

        private static string HashCode(string pendingId, string challengeHash, string code)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{pendingId}|{challengeHash}|{code}"));
            return Convert.ToHexString(bytes);
        }

        private static string ChallengeKey(string challengeHash) => $"auth:stepup:challenge:{challengeHash}";
        private static string EmailTokenKey(string emailTokenHash) => $"auth:stepup:email:{emailTokenHash}";
        private static string CompletionLockKey(string pendingId) => $"auth:stepup:lock:{pendingId}";
        private static string UserSendKey(int userId) => $"auth:stepup:send:user:{userId}";
        private static string IpSendKey(string ipAddress) => $"auth:stepup:send:ip:{ipAddress}";

        private sealed class PendingStepUpState
        {
            public required string PendingId
            {
                get; set;
            }
            public required string ChallengeHash
            {
                get; set;
            }
            /// <summary>
            /// The account's auth version when the challenge began. A challenge that outlives a
            /// security-sensitive change must not still be able to mint a session.
            /// </summary>
            public int AuthVersion
            {
                get; set;
            }
            public int UserId
            {
                get; set;
            }
            public required string Email
            {
                get; set;
            }
            public string? PhoneNumber
            {
                get; set;
            }
            public bool HasTotp
            {
                get; set;
            }
            public SessionTransport Transport
            {
                get; set;
            }
            public bool RememberMe
            {
                get; set;
            }
            public required string TrustedDeviceId
            {
                get; set;
            }
            public required string DeviceType
            {
                get; set;
            }
            public required string ClientName
            {
                get; set;
            }
            public required string IpAddress
            {
                get; set;
            }
            public required string ReturnPath
            {
                get; set;
            }
            public DateTime ExpiresAtUtc
            {
                get; set;
            }
            public string? SmsCodeHash
            {
                get; set;
            }
            public string? EmailTokenHash
            {
                get; set;
            }
            public int SmsFailedAttempts
            {
                get; set;
            }
            public int TotpFailedAttempts
            {
                get; set;
            }
            public required DeliveryState Sms
            {
                get; set;
            }
            public required DeliveryState EmailDelivery
            {
                get; set;
            }
        }

        private sealed class DeliveryState
        {
            public int SendCount
            {
                get; set;
            }
            public DateTime? CooldownEndsAtUtc
            {
                get; set;
            }
        }
    }
}
