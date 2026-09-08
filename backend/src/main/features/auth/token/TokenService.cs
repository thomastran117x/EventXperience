using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using backend.main.application.environment;
using backend.main.application.security;
using backend.main.features.cache;
using backend.main.features.profile;
using backend.main.shared.exceptions.http;
using backend.main.shared.requests;
using backend.main.shared.utilities;
using backend.main.shared.utilities.logger;

using Microsoft.IdentityModel.Tokens;

using Newtonsoft.Json;

namespace backend.main.features.auth.token
{
    public class TokenService : ITokenService
    {
        public const string AuthVersionClaimType = "auth_version";
        public const string SessionIdClaimType = "sid";
        private readonly JwtSecurityTokenHandler _tokenHandler = new();
        private readonly string JWT_ACCESS_SECRET;
        private readonly string JWT_VERIFICATION_SECRET;
        private readonly TimeSpan JWT_ACCESS_LIFETIME = TimeSpan.FromMinutes(15);
        private const string ISSUER = "EventXperience";
        private const string AUDIENCE = "EventXperienceConsumers";
        private const string VERIFICATION_AUDIENCE = "EventXperienceVerification";
        private readonly ICacheService _cacheService;
        private const string RefreshKeyPrefix = "refresh:v2";
        private readonly TimeSpan DEFAULT_REFRESH_TTL = TimeSpan.FromDays(1);
        private readonly TimeSpan REMEMBERED_REFRESH_TTL = TimeSpan.FromDays(30);
        private readonly TimeSpan VERIFY_TTL = TimeSpan.FromMinutes(30);
        private const int MAX_OTP_ATTEMPTS = 5;
        private const string PlaceholderUsertype = "placeholder";
        private static readonly TimeSpan EmailChangeLockTtl = TimeSpan.FromSeconds(10);

        public TokenService(ICacheService cacheService)
        {
            JWT_ACCESS_SECRET = EnvironmentSetting.JwtSecretKeyAccess;
            JWT_VERIFICATION_SECRET = EnvironmentSetting.JwtSecretKeyVerification;
            _cacheService = cacheService;
        }

        public AccessTokenIssue GenerateAccessToken(User user, string sessionId)
        {
            try
            {
                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JWT_ACCESS_SECRET));
                var expiresAtUtc = DateTime.UtcNow.Add(JWT_ACCESS_LIFETIME);

                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Name, user.Email),
                    new Claim(ClaimTypes.Role, AuthRoles.NormalizeStored(user.Usertype)),
                    new Claim(AuthVersionClaimType, user.AuthVersion.ToString()),
                    new Claim(SessionIdClaimType, sessionId),
                };

                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(claims),
                    Expires = expiresAtUtc,
                    SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256Signature),
                    Issuer = ISSUER,
                    Audience = AUDIENCE
                };

                var token = _tokenHandler.CreateToken(tokenDescriptor);
                return new AccessTokenIssue(_tokenHandler.WriteToken(token), expiresAtUtc);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] GenerateAccessToken failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<RefreshTokenIssue> GenerateRefreshToken(
            int userId,
            ClientRequestInfo requestInfo,
            SessionTransport transport,
            string? sessionId = null,
            bool? rememberMe = null
        )
        {
            try
            {
                string refreshToken;
                string refreshTokenHash;
                string sessionBindingToken;
                string sessionBindingTokenHash;
                TimeSpan refreshTtl;

                do
                {
                    refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                    refreshTokenHash = ComputeTokenHash(refreshToken);

                    string? existing = await _cacheService.GetValueAsync(TokenKey(refreshTokenHash));

                    if (existing == null)
                        break;
                }

                while (true);

                RefreshSessionState session;
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    session = new RefreshSessionState
                    {
                        SessionId = Guid.NewGuid().ToString("N"),
                        UserId = userId,
                        Transport = transport,
                        LastSeenDeviceType = requestInfo.DeviceType,
                        LastSeenClientName = requestInfo.ClientName,
                        LastSeenIpAddress = requestInfo.IpAddress,
                        CreatedAt = DateTime.UtcNow,
                        LastSeenAt = DateTime.UtcNow,
                        CurrentRefreshTokenHash = string.Empty,
                        CurrentBindingTokenHash = string.Empty,
                        RememberMe = rememberMe ?? false,
                    };
                    refreshTtl = ResolveRefreshTtl(session.RememberMe);
                }
                else
                {
                    session = await GetRefreshSessionAsync(sessionId)
                        ?? throw new UnauthorizedException("Refresh session is invalid or expired.");

                    if (session.UserId != userId)
                        throw new UnauthorizedException("Refresh session user mismatch.");

                    if (session.Transport != transport)
                        throw new UnauthorizedException("Refresh session transport mismatch.");

                    session.LastSeenDeviceType = requestInfo.DeviceType;
                    session.LastSeenClientName = requestInfo.ClientName;
                    session.LastSeenIpAddress = requestInfo.IpAddress;
                    session.LastSeenAt = DateTime.UtcNow;
                    if (rememberMe.HasValue)
                        session.RememberMe = rememberMe.Value;

                    refreshTtl = ResolveRefreshTtl(session.RememberMe);
                }

                sessionBindingToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                sessionBindingTokenHash = ComputeTokenHash(sessionBindingToken);
                session.CurrentRefreshTokenHash = refreshTokenHash;
                session.CurrentBindingTokenHash = sessionBindingTokenHash;

                var tokenRecord = new RefreshTokenRecord
                {
                    SessionId = session.SessionId,
                    UserId = userId,
                    CreatedAt = DateTime.UtcNow,
                };

                var tokenResult = await _cacheService.SetValueAsync(
                    key: TokenKey(refreshTokenHash),
                    value: JsonConvert.SerializeObject(tokenRecord),
                    expiry: refreshTtl
                );

                var sessionResult = await _cacheService.SetValueAsync(
                    key: SessionKey(session.SessionId),
                    value: JsonConvert.SerializeObject(session),
                    expiry: refreshTtl
                );

                await _cacheService.SetAddAsync(UserSessionsKey(userId), session.SessionId);

                if (!tokenResult || !sessionResult)
                    throw new NotAvailableException();

                return new RefreshTokenIssue(
                    refreshToken,
                    sessionBindingToken,
                    refreshTtl,
                    session.Transport,
                    session.SessionId
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] GenerateRefreshToken failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<RefreshTokenValidationResult> ValidateRefreshToken(
            string refreshToken,
            string? sessionBindingToken,
            SessionTransport expectedTransport,
            ClientRequestInfo requestInfo
        )
        {
            try
            {
                var tokenHash = ComputeTokenHash(refreshToken);
                string? storedValue = await _cacheService.GetValueAsync(TokenKey(tokenHash));

                if (string.IsNullOrEmpty(storedValue))
                    throw new UnauthorizedException("Invalid or expired refresh token.");

                var tokenRecord = JsonConvert.DeserializeObject<RefreshTokenRecord>(storedValue)
                    ?? throw new UnauthorizedException("Invalid refresh token payload.");

                var session = await GetRefreshSessionAsync(tokenRecord.SessionId)
                    ?? throw new UnauthorizedException("Refresh session is invalid or expired.");

                if (session.UserId != tokenRecord.UserId)
                {
                    await RevokeRefreshSessionAsync(tokenRecord.SessionId);
                    throw new UnauthorizedException("Refresh session user mismatch.");
                }

                if (session.Transport != expectedTransport)
                {
                    await RevokeRefreshSessionAsync(tokenRecord.SessionId);
                    throw new UnauthorizedException("Refresh token transport mismatch.");
                }

                if (session.CurrentRefreshTokenHash != tokenHash)
                {
                    await RevokeRefreshSessionAsync(tokenRecord.SessionId);
                    throw new UnauthorizedException("Refresh token reuse detected.");
                }

                if (string.IsNullOrWhiteSpace(sessionBindingToken))
                {
                    await RevokeRefreshSessionAsync(tokenRecord.SessionId);
                    throw new UnauthorizedException("Missing session binding token.");
                }

                if (session.CurrentBindingTokenHash != ComputeTokenHash(sessionBindingToken))
                {
                    await RevokeRefreshSessionAsync(tokenRecord.SessionId);
                    throw new UnauthorizedException("Invalid session binding token.");
                }

                var result = await _cacheService.DeleteKeyAsync(TokenKey(tokenHash));
                if (!result)
                    throw new NotAvailableException();

                session.LastSeenAt = DateTime.UtcNow;
                session.LastSeenIpAddress = requestInfo.IpAddress;
                session.LastSeenClientName = requestInfo.ClientName;
                session.LastSeenDeviceType = requestInfo.DeviceType;

                var sessionUpdated = await _cacheService.SetValueAsync(
                    SessionKey(session.SessionId),
                    JsonConvert.SerializeObject(session),
                    ResolveRefreshTtl(session.RememberMe)
                );

                if (!sessionUpdated)
                    throw new NotAvailableException();

                return new RefreshTokenValidationResult
                {
                    SessionId = session.SessionId,
                    UserId = tokenRecord.UserId,
                    Transport = session.Transport,
                };
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] ValidateRefreshToken failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<string> GenerateVerificationToken(User user, VerificationPurpose purpose)
        {
            try
            {
                var artifacts = await GenerateVerificationArtifactsAsync(user, purpose);
                return artifacts.LinkToken;
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] GenerateVerificationToken failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<VerificationOtpChallenge> GenerateVerificationOtpAsync(
            User user,
            VerificationPurpose purpose
        )
        {
            try
            {
                var artifacts = await GenerateVerificationArtifactsAsync(user, purpose);
                return artifacts.OtpChallenge;
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] GenerateVerificationOtpAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<VerificationArtifacts> GenerateVerificationArtifactsAsync(
            User user,
            VerificationPurpose purpose,
            bool replaceExisting = false
        )
        {
            try
            {
                var existingState = await GetVerificationStateAsync(user.Email, purpose);
                var usernameMatches = purpose != VerificationPurpose.SignUp
                    || string.Equals(
                        existingState?.Username,
                        user.Username,
                        StringComparison.Ordinal
                    );

                if (existingState != null && !replaceExisting && usernameMatches)
                {
                    return new VerificationArtifacts
                    {
                        LinkToken = existingState.LinkToken,
                        OtpChallenge = new VerificationOtpChallenge
                        {
                            Code = existingState.OtpCode,
                            Challenge = existingState.OtpChallenge,
                            ExpiresAtUtc = existingState.ExpiresAtUtc,
                        },
                        Purpose = existingState.Purpose,
                    };
                }

                if (existingState != null)
                {
                    _ = await _cacheService.DeleteKeyAsync(
                        VerificationTokenKey(existingState.LinkToken)
                    );
                    await DeleteVerificationStateAsync(existingState);
                }

                string linkToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                string otpCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                DateTime expiresAtUtc = DateTime.UtcNow.Add(VERIFY_TTL);
                var payload = BuildVerificationPayload(user, purpose);
                string challenge = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
                var otpProof = ComputeOtpProof(
                    purpose,
                    payload.Email,
                    payload.Password,
                    payload.Usertype,
                    payload.Username,
                    expiresAtUtc,
                    challenge,
                    otpCode,
                    payload.UserId
                );

                var linkStored = await _cacheService.SetValueAsync(
                    key: VerificationTokenKey(linkToken),
                    value: JsonConvert.SerializeObject(payload),
                    expiry: VERIFY_TTL
                );

                var state = new VerificationDeliveryState
                {
                    Email = user.Email,
                    UserId = payload.UserId,
                    AuthVersion = payload.AuthVersion,
                    Purpose = purpose,
                    LinkToken = linkToken,
                    OtpCode = otpCode,
                    OtpChallenge = challenge,
                    OtpProof = otpProof,
                    Password = payload.Password,
                    Usertype = payload.Usertype,
                    Username = payload.Username,
                    ExpiresAtUtc = expiresAtUtc,
                };

                var stateJson = JsonConvert.SerializeObject(state);

                var stateStored = await _cacheService.SetValueAsync(
                    key: VerificationStateKey(user.Email, purpose),
                    value: stateJson,
                    expiry: VERIFY_TTL
                );

                var challengeStored = await _cacheService.SetValueAsync(
                    key: VerificationChallengeKey(challenge),
                    value: stateJson,
                    expiry: VERIFY_TTL
                );

                if (!linkStored || !stateStored || !challengeStored)
                    throw new NotAvailableException();

                return new VerificationArtifacts
                {
                    LinkToken = linkToken,
                    OtpChallenge = new VerificationOtpChallenge
                    {
                        Code = otpCode,
                        Challenge = challenge,
                        ExpiresAtUtc = expiresAtUtc,
                    },
                    Purpose = purpose,
                };
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] GenerateVerificationArtifactsAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<User> VerifyVerificationToken(string token, VerificationPurpose expectedPurpose)
        {
            try
            {
                string? json = await _cacheService.GetValueAsync(VerificationTokenKey(token));

                if (string.IsNullOrEmpty(json))
                    throw new UnauthorizedException("Invalid or expired verification token.");

                var payload = JsonConvert.DeserializeObject<VerificationTokenPayload>(json)
                    ?? throw new UnauthorizedException("Invalid verification token payload.");

                if (payload.Purpose != expectedPurpose)
                    throw new UnauthorizedException("Verification token purpose mismatch.");

                _ = await _cacheService.DeleteKeyAsync(VerificationTokenKey(token));
                var state = await GetVerificationStateAsync(payload.Email, payload.Purpose);
                if (state != null)
                    await DeleteVerificationStateAsync(state);
                else
                    _ = await _cacheService.DeleteKeyAsync(
                        VerificationStateKey(payload.Email, payload.Purpose)
                    );

                return CreateUserFromPayload(payload);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] VerifyVerificationToken failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<User> VerifyVerificationOtpAsync(
            string code,
            string challenge,
            VerificationPurpose expectedPurpose
        )
        {
            try
            {
                var state = await GetVerificationStateByChallengeAsync(challenge);
                if (state == null)
                    throw new UnauthorizedException("Invalid or expired verification challenge.");

                if (state.Purpose != expectedPurpose)
                    throw new UnauthorizedException("Verification challenge purpose mismatch.");

                var expectedProof = ComputeOtpProof(
                    state.Purpose,
                    state.Email,
                    state.Password,
                    state.Usertype,
                    state.Username,
                    state.ExpiresAtUtc,
                    challenge,
                    code,
                    state.UserId
                );

                if (state.OtpChallenge != challenge)
                    throw new UnauthorizedException("Invalid or expired verification challenge.");

                if (!CryptoHelper.FixedTimeEquals(state.OtpProof, expectedProof))
                {
                    var attempts = await RecordFailedOtpAttemptAsync(challenge);
                    if (attempts >= MAX_OTP_ATTEMPTS)
                    {
                        _ = await _cacheService.DeleteKeyAsync(VerificationTokenKey(state.LinkToken));
                        await DeleteVerificationStateAsync(state);
                    }

                    throw new UnauthorizedException("Invalid or expired verification code.");
                }

                _ = await _cacheService.DeleteKeyAsync(VerificationTokenKey(state.LinkToken));
                await DeleteVerificationStateAsync(state);
                _ = await _cacheService.DeleteKeyAsync(OtpAttemptKey(challenge));

                return CreateUserFromPayload(new VerificationTokenPayload
                {
                    Email = state.Email,
                    UserId = state.UserId,
                    AuthVersion = state.AuthVersion,
                    Password = state.Password,
                    Usertype = state.Usertype,
                    Username = state.Username,
                    Purpose = state.Purpose,
                });
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] VerifyVerificationOtpAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<string?> VerificationTokenExist(string email, VerificationPurpose purpose)
        {
            try
            {
                var state = await GetVerificationStateAsync(email, purpose);
                return state?.LinkToken;
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] VerificationTokenExist failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        /// <summary>
        /// Issues the link token and OTP for an email change. Unlike signup and reset, the state
        /// is keyed by an address the account does not own yet, so the artifacts are also indexed
        /// by account id - see <c>EmailChangeIndexKey</c>.
        /// </summary>
        public async Task<VerificationArtifacts> GenerateEmailChangeArtifactsAsync(
            int userId,
            int authVersion,
            string newEmail
        )
        {
            // Cancel, generate and index have to happen as one unit, and consumption has to take
            // the same lock. Each pending change is stored under its own target address, so
            // without this two concurrent requests both clear the same (empty) index and both mint
            // a redeemable token, and a consumption racing a generation deletes the index the
            // generation just wrote - in both cases leaving a live proof that neither the pending
            // endpoint nor a later cancel can reach.
            return await WithEmailChangeLockAsync(userId, async () =>
            {
                await CancelPendingEmailChangeCoreAsync(userId);

                var artifacts = await GenerateVerificationArtifactsAsync(
                    new User
                    {
                        Id = userId,
                        AuthVersion = authVersion,
                        Email = newEmail,
                        Usertype = PlaceholderUsertype,
                    },
                    VerificationPurpose.ChangeEmail,
                    replaceExisting: true
                );

                var indexed = await _cacheService.SetValueAsync(
                    key: EmailChangeIndexKey(userId),
                    value: newEmail,
                    expiry: VERIFY_TTL
                );

                if (!indexed)
                    throw new NotAvailableException();

                return artifacts;
            });
        }

        public async Task<PendingEmailChange> ConsumeEmailChangeTokenAsync(string token)
        {
            try
            {
                // Read the payload first, only to learn whose lock to take.
                string? peek = await _cacheService.GetValueAsync(VerificationTokenKey(token));
                if (string.IsNullOrEmpty(peek))
                    throw new UnauthorizedException("Invalid or expired email change link.");

                var peeked = JsonConvert.DeserializeObject<VerificationTokenPayload>(peek)
                    ?? throw new UnauthorizedException("Invalid verification token payload.");

                // Checked before the account id so a token for another purpose is reported as
                // such, rather than as one missing a binding it was never meant to carry.
                if (peeked.Purpose != VerificationPurpose.ChangeEmail)
                    throw new UnauthorizedException("Verification token purpose mismatch.");

                if (peeked.UserId is not int owner)
                    throw new UnauthorizedException("Email change token is missing its account.");

                return await WithEmailChangeLockAsync(owner, () => ConsumeTokenCoreAsync(token));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] ConsumeEmailChangeTokenAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        private async Task<PendingEmailChange> ConsumeTokenCoreAsync(string token)
        {
            try
            {
                string? json = await _cacheService.GetValueAsync(VerificationTokenKey(token));

                if (string.IsNullOrEmpty(json))
                    throw new UnauthorizedException("Invalid or expired email change link.");

                var payload = JsonConvert.DeserializeObject<VerificationTokenPayload>(json)
                    ?? throw new UnauthorizedException("Invalid verification token payload.");

                if (payload.Purpose != VerificationPurpose.ChangeEmail)
                    throw new UnauthorizedException("Verification token purpose mismatch.");

                if (payload.UserId is not int userId)
                    throw new UnauthorizedException("Email change token is missing its account.");

                if (payload.AuthVersion is not int authVersion)
                    throw new UnauthorizedException("Email change token is missing its account.");

                var state = await GetVerificationStateAsync(payload.Email, payload.Purpose);
                var expiresAtUtc = state?.ExpiresAtUtc ?? DateTime.UtcNow;

                _ = await _cacheService.DeleteKeyAsync(VerificationTokenKey(token));
                if (state != null)
                    await DeleteVerificationStateAsync(state);
                else
                    _ = await _cacheService.DeleteKeyAsync(
                        VerificationStateKey(payload.Email, payload.Purpose)
                    );

                _ = await _cacheService.DeleteKeyAsync(EmailChangeIndexKey(userId));

                return new PendingEmailChange(userId, authVersion, payload.Email, expiresAtUtc);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] ConsumeTokenCoreAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<PendingEmailChange> ConsumeEmailChangeOtpAsync(
            string code,
            string challenge
        )
        {
            try
            {
                var peek = await GetVerificationStateByChallengeAsync(challenge)
                    ?? throw new UnauthorizedException("Invalid or expired verification challenge.");

                if (peek.Purpose != VerificationPurpose.ChangeEmail)
                    throw new UnauthorizedException("Verification challenge purpose mismatch.");

                if (peek.UserId is not int owner)
                    throw new UnauthorizedException("Email change challenge is missing its account.");

                return await WithEmailChangeLockAsync(
                    owner,
                    () => ConsumeOtpCoreAsync(code, challenge));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] ConsumeEmailChangeOtpAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        private async Task<PendingEmailChange> ConsumeOtpCoreAsync(string code, string challenge)
        {
            try
            {
                var state = await GetVerificationStateByChallengeAsync(challenge);
                if (state == null)
                    throw new UnauthorizedException("Invalid or expired verification challenge.");

                if (state.Purpose != VerificationPurpose.ChangeEmail)
                    throw new UnauthorizedException("Verification challenge purpose mismatch.");

                if (state.UserId is not int userId)
                    throw new UnauthorizedException("Email change challenge is missing its account.");

                if (state.AuthVersion is not int authVersion)
                    throw new UnauthorizedException("Email change challenge is missing its account.");

                if (state.OtpChallenge != challenge)
                    throw new UnauthorizedException("Invalid or expired verification challenge.");

                var expectedProof = ComputeOtpProof(
                    state.Purpose,
                    state.Email,
                    state.Password,
                    state.Usertype,
                    state.Username,
                    state.ExpiresAtUtc,
                    challenge,
                    code,
                    state.UserId
                );

                if (!CryptoHelper.FixedTimeEquals(state.OtpProof, expectedProof))
                {
                    var attempts = await RecordFailedOtpAttemptAsync(challenge);
                    if (attempts >= MAX_OTP_ATTEMPTS)
                    {
                        _ = await _cacheService.DeleteKeyAsync(VerificationTokenKey(state.LinkToken));
                        await DeleteVerificationStateAsync(state);
                        _ = await _cacheService.DeleteKeyAsync(EmailChangeIndexKey(userId));
                    }

                    throw new UnauthorizedException("Invalid or expired verification code.");
                }

                _ = await _cacheService.DeleteKeyAsync(VerificationTokenKey(state.LinkToken));
                await DeleteVerificationStateAsync(state);
                _ = await _cacheService.DeleteKeyAsync(OtpAttemptKey(challenge));
                _ = await _cacheService.DeleteKeyAsync(EmailChangeIndexKey(userId));

                return new PendingEmailChange(userId, authVersion, state.Email, state.ExpiresAtUtc);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] ConsumeOtpCoreAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<PendingEmailChange?> GetPendingEmailChangeAsync(int userId)
        {
            try
            {
                var email = await _cacheService.GetValueAsync(EmailChangeIndexKey(userId));
                if (string.IsNullOrWhiteSpace(email))
                    return null;

                var state = await GetVerificationStateAsync(email, VerificationPurpose.ChangeEmail);

                // The index can outlive the state it points at: the state is deleted on redemption
                // and after too many failed OTP attempts, and both keys expire independently.
                if (state == null || state.UserId != userId || state.AuthVersion is not int version)
                {
                    _ = await _cacheService.DeleteKeyAsync(EmailChangeIndexKey(userId));
                    return null;
                }

                return new PendingEmailChange(userId, version, state.Email, state.ExpiresAtUtc);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] GetPendingEmailChangeAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public Task CancelPendingEmailChangeAsync(int userId) =>
            WithEmailChangeLockAsync<object?>(userId, async () =>
            {
                await CancelPendingEmailChangeCoreAsync(userId);
                return null;
            });

        private async Task CancelPendingEmailChangeCoreAsync(int userId)
        {
            try
            {
                var email = await _cacheService.GetValueAsync(EmailChangeIndexKey(userId));

                if (!string.IsNullOrWhiteSpace(email))
                {
                    var state = await GetVerificationStateAsync(
                        email,
                        VerificationPurpose.ChangeEmail
                    );

                    if (state != null && state.UserId == userId)
                    {
                        _ = await _cacheService.DeleteKeyAsync(
                            VerificationTokenKey(state.LinkToken)
                        );
                        await DeleteVerificationStateAsync(state);
                    }
                }

                _ = await _cacheService.DeleteKeyAsync(EmailChangeIndexKey(userId));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] CancelPendingEmailChangeCoreAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<TimeSpan?> GetRefreshSessionTtlAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return null;

            return await _cacheService.GetTTLAsync(SessionKey(sessionId));
        }

        public async Task RevokeRefreshSessionAsync(string sessionId)
        {
            try
            {
                var session = await GetRefreshSessionAsync(sessionId);
                if (session == null)
                    return;

                if (!string.IsNullOrWhiteSpace(session.CurrentRefreshTokenHash))
                    await _cacheService.DeleteKeyAsync(TokenKey(session.CurrentRefreshTokenHash));

                await _cacheService.DeleteKeyAsync(SessionKey(session.SessionId));
                await _cacheService.SetRemoveAsync(UserSessionsKey(session.UserId), session.SessionId);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] RevokeRefreshSessionAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task RevokeAllRefreshSessionsAsync(int userId)
        {
            try
            {
                var sessionIds = await _cacheService.SetMembersAsync(UserSessionsKey(userId));
                foreach (var sessionId in sessionIds)
                    await RevokeRefreshSessionAsync(sessionId);

                await _cacheService.DeleteKeyAsync(UserSessionsKey(userId));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[TokenService] RevokeAllRefreshSessionsAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        private async Task<RefreshSessionState?> GetRefreshSessionAsync(string sessionId)
        {
            var json = await _cacheService.GetValueAsync(SessionKey(sessionId));
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonConvert.DeserializeObject<RefreshSessionState>(json);
        }

        private VerificationTokenPayload BuildVerificationPayload(
            User user,
            VerificationPurpose purpose
        )
        {
            return new VerificationTokenPayload
            {
                Email = user.Email,
                // Signup builds an in-memory user that has never been inserted, so its Id is 0.
                // Only an email change supplies a real account id here.
                UserId = user.Id > 0 ? user.Id : null,
                AuthVersion = user.Id > 0 ? user.AuthVersion : null,
                Password = purpose == VerificationPurpose.SignUp ? user.Password : null,
                Username = purpose == VerificationPurpose.SignUp ? user.Username : null,
                Usertype = purpose == VerificationPurpose.SignUp
                    ? AuthRoles.NormalizeStored(user.Usertype)
                    : PlaceholderUsertype,
                Purpose = purpose,
            };
        }

        private async Task<VerificationDeliveryState?> GetVerificationStateAsync(
            string email,
            VerificationPurpose purpose
        )
        {
            var json = await _cacheService.GetValueAsync(VerificationStateKey(email, purpose));
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonConvert.DeserializeObject<VerificationDeliveryState>(json);
        }

        private async Task<VerificationDeliveryState?> GetVerificationStateByChallengeAsync(
            string challenge
        )
        {
            var json = await _cacheService.GetValueAsync(VerificationChallengeKey(challenge));
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonConvert.DeserializeObject<VerificationDeliveryState>(json);
        }

        private async Task DeleteVerificationStateAsync(VerificationDeliveryState state)
        {
            _ = await _cacheService.DeleteKeyAsync(VerificationStateKey(state.Email, state.Purpose));
            _ = await _cacheService.DeleteKeyAsync(VerificationChallengeKey(state.OtpChallenge));
            _ = await _cacheService.DeleteKeyAsync(OtpAttemptKey(state.OtpChallenge));
        }

        private User CreateUserFromPayload(VerificationTokenPayload payload)
        {
            return new User
            {
                Email = payload.Email,
                Password = payload.Password,
                Username = payload.Username,
                Usertype = AuthRoles.NormalizeStored(payload.Usertype),
            };
        }

        private static string ComputeTokenHash(string token) => CryptoHelper.HashToken(token);

        private string ComputeOtpProof(
            VerificationPurpose purpose,
            string email,
            string? password,
            string? usertype,
            string? username,
            DateTime expiresAtUtc,
            string challenge,
            string otpCode,
            int? userId = null
        )
        {
            var fields = new List<object?>
            {
                purpose,
                email,
                password ?? string.Empty,
                usertype ?? string.Empty,
            };
            if (username != null)
                fields.Add(username);
            fields.Add(expiresAtUtc.ToUniversalTime().Ticks);
            fields.Add(challenge);
            fields.Add(otpCode);
            // Appended last and only when present, so proofs already issued for the signup and
            // reset purposes hash to the same value across the deploy that adds this.
            if (userId.HasValue)
                fields.Add(userId.Value);

            var material = string.Join("|", fields);

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(JWT_VERIFICATION_SECRET));
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(material)));
        }

        private TimeSpan ResolveRefreshTtl(bool rememberMe) =>
            rememberMe ? REMEMBERED_REFRESH_TTL : DEFAULT_REFRESH_TTL;

        private async Task<long> RecordFailedOtpAttemptAsync(string challenge)
        {
            var key = OtpAttemptKey(challenge);
            var attempts = await _cacheService.IncrementAsync(key);
            _ = await _cacheService.SetExpiryAsync(key, VERIFY_TTL);
            return attempts;
        }

        private static string TokenKey(string tokenHash) =>
            $"{RefreshKeyPrefix}:token:{tokenHash}";

        private static string SessionKey(string sessionId) =>
            $"{RefreshKeyPrefix}:session:{sessionId}";

        private static string UserSessionsKey(int userId) =>
            $"{RefreshKeyPrefix}:user:{userId}:sessions";

        private static string VerificationTokenKey(string token) => $"verify:token:{token}";

        private static string VerificationStateKey(string email, VerificationPurpose purpose) =>
            $"verify:email:{purpose}:{email}";

        private static string VerificationChallengeKey(string challenge) =>
            $"verify:challenge:{challenge}";

        private static string OtpAttemptKey(string challenge) =>
            $"verify:otp-attempt:{challenge}";

        // Reverse index from account to pending target address. The verification state itself is
        // keyed by the NEW email, which nothing knows until the change is confirmed, so without
        // this a user could neither be shown their pending change nor cancel it — and a second
        // request to a different address would strand the first one until its TTL ran out.
        private static string EmailChangeIndexKey(int userId) =>
            $"verify:user-email-change:{userId}";

        private async Task<T> WithEmailChangeLockAsync<T>(int userId, Func<Task<T>> action)
        {
            var lockValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var lockKey = EmailChangeLockKey(userId);
            if (!await _cacheService.AcquireLockAsync(lockKey, lockValue, EmailChangeLockTtl))
                throw new ConflictException(
                    "Another email change request is already being processed."
                );

            try
            {
                return await action();
            }
            finally
            {
                await _cacheService.ReleaseLockAsync(lockKey, lockValue);
            }
        }

        private static string EmailChangeLockKey(int userId) =>
            $"verify:user-email-change-lock:{userId}";

        private sealed class RefreshTokenRecord
        {
            public required string SessionId
            {
                get; set;
            }
            public int UserId
            {
                get; set;
            }
            public DateTime CreatedAt
            {
                get; set;
            }
        }

        private sealed class RefreshSessionState
        {
            public required string SessionId
            {
                get; set;
            }
            public int UserId
            {
                get; set;
            }
            public SessionTransport Transport
            {
                get; set;
            }
            public required string CurrentRefreshTokenHash
            {
                get; set;
            }
            public required string CurrentBindingTokenHash
            {
                get; set;
            }
            public bool RememberMe
            {
                get; set;
            }
            public string LastSeenIpAddress { get; set; } = "Unknown";
            public string LastSeenClientName { get; set; } = "Unknown";
            public string LastSeenDeviceType { get; set; } = "Unknown";
            public DateTime CreatedAt
            {
                get; set;
            }
            public DateTime LastSeenAt
            {
                get; set;
            }
        }

        private sealed class VerificationTokenPayload
        {
            public required string Email
            {
                get; set;
            }
            /// <summary>
            /// Set only for <see cref="VerificationPurpose.ChangeEmail"/>, where the account
            /// already exists. Null for signup and reset, whose payloads describe a user that is
            /// either not yet persisted or identified by email alone.
            /// </summary>
            public int? UserId
            {
                get; set;
            }
            public int? AuthVersion
            {
                get; set;
            }
            public string? Password
            {
                get; set;
            }
            public string? Username
            {
                get; set;
            }
            public required string Usertype
            {
                get; set;
            }
            public VerificationPurpose Purpose
            {
                get; set;
            }
        }

        private sealed class VerificationDeliveryState
        {
            public required string Email
            {
                get; set;
            }
            public int? UserId
            {
                get; set;
            }
            public int? AuthVersion
            {
                get; set;
            }
            public VerificationPurpose Purpose
            {
                get; set;
            }
            public required string LinkToken
            {
                get; set;
            }
            public required string OtpCode
            {
                get; set;
            }
            public required string OtpChallenge
            {
                get; set;
            }
            public required string OtpProof
            {
                get; set;
            }
            public string? Password
            {
                get; set;
            }
            public string? Username
            {
                get; set;
            }
            public required string Usertype
            {
                get; set;
            }
            public DateTime ExpiresAtUtc
            {
                get; set;
            }
        }
    }
}

