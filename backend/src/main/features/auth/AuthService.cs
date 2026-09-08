using System.Security.Cryptography;

using backend.main.application.environment;
using backend.main.application.security;
using backend.main.features.auth.contracts;
using backend.main.features.auth.contracts.responses;
using backend.main.features.auth.device;
using backend.main.features.auth.mfa.totp;
using backend.main.features.auth.notifications;
using backend.main.features.auth.oauth;
using backend.main.features.auth.stepup;
using backend.main.features.auth.token;
using backend.main.features.cache;
using backend.main.features.profile;
using backend.main.shared.exceptions.http;
using backend.main.shared.requests;
using backend.main.shared.utilities.logger;

using Microsoft.AspNetCore.WebUtilities;

using Newtonsoft.Json;

namespace backend.main.features.auth
{
    public class AuthService : IAuthService
    {
        private readonly IAuthUserRepository _userRepository;
        private readonly IOAuthService _oauthService;
        private readonly ITokenService _tokenService;
        private readonly ICacheService _cacheService;
        private readonly IAuthNotificationService _authNotificationService;
        private readonly IDeviceService _deviceService;
        private readonly ITotpMfaEnrollmentService _totpMfaEnrollmentService;
        private readonly IDeviceTrustService _deviceTrustService;
        private readonly ILoginStepUpChallengeService _loginStepUpChallengeService;
        private readonly IAuthSessionService _authSessionService;
        private readonly IUsernameAvailabilityService _usernameAvailability;
        private readonly IEmailAvailabilityService _emailAvailability;
        private readonly SeedAccountBypassPolicy _seedBypass;
        private readonly ClientRequestInfo _requestInfo;
        private const string DummyHash = "$2a$11$9FJqO6j/4jP3E2fOQdWgMuKZXWWvPZ09f8Pj0L9VqB6TfqZ4fE5SO";
        private static readonly TimeSpan PendingOAuthSignupTtl = TimeSpan.FromMinutes(15);

        public AuthService(
            IAuthUserRepository userRepository,
            IOAuthService oauthService,
            ITokenService tokenService,
            ICacheService cacheService,
            IAuthNotificationService authNotificationService,
            IDeviceService deviceService,
            ITotpMfaEnrollmentService totpMfaEnrollmentService,
            IDeviceTrustService deviceTrustService,
            ILoginStepUpChallengeService loginStepUpChallengeService,
            IAuthSessionService authSessionService,
            IUsernameAvailabilityService usernameAvailability,
            IEmailAvailabilityService emailAvailability,
            SeedAccountBypassPolicy seedBypass,
            ClientRequestInfo requestInfo
        )
        {
            _userRepository = userRepository;
            _oauthService = oauthService;
            _tokenService = tokenService;
            _cacheService = cacheService;
            _authNotificationService = authNotificationService;
            _deviceService = deviceService;
            _totpMfaEnrollmentService = totpMfaEnrollmentService;
            _deviceTrustService = deviceTrustService;
            _loginStepUpChallengeService = loginStepUpChallengeService;
            _authSessionService = authSessionService;
            _usernameAvailability = usernameAvailability;
            _emailAvailability = emailAvailability;
            _seedBypass = seedBypass;
            _requestInfo = requestInfo;
        }

        public async Task<LoginAuthenticationResult> LoginAsync(
            string username,
            string password,
            SessionTransport transport,
            bool rememberMe = false,
            string? returnUrl = null
        )
        {
            try
            {
                var normalizedUsername = UsernamePolicy.Normalize(username);
                UserAuthRecord? user = string.IsNullOrEmpty(normalizedUsername)
                    ? null
                    : await _userRepository.GetAuthByUsernameAsync(normalizedUsername);

                var hashToCheck = user?.Password ?? DummyHash;
                bool isValidPassword = VerifyPassword(password, hashToCheck);

                if (user == null || user.Password == null || !isValidPassword)
                    throw new UnauthorizedException("Invalid username or password");

                var resolvedUser = ToUser(user);
                await EnsureUserEnabledAsync(resolvedUser);

                return await ResolvePostAuthOutcomeAsync(
                    resolvedUser, transport, rememberMe, returnUrl,
                    LoginAuthenticationResult.Authenticated,
                    LoginAuthenticationResult.RequiresStepUp
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] LoginAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<VerificationOtpChallenge> SignUpAsync(
            string email,
            string username,
            string password,
            string userType)
        {
            try
            {
                var usernameForms = UsernamePolicy.NormalizeAndValidateWithDisplay(username);
                username = usernameForms.Username;

                // Only the lookup gets the lowercased form. The address carried forward keeps the
                // casing the user typed, because it is the one we store and deliver mail to, and
                // RFC 5321 leaves the local part case-sensitive to the destination host.
                email = EmailPolicy.Sanitize(email);
                var probeEmail = EmailPolicy.NormalizeAndValidate(email);

                // Advisory: this method sends a verification mail, it does not create the account.
                // The verify step below re-checks authoritatively before inserting, so a filter
                // that has not yet seen a signup from another instance only defers the conflict
                // rather than admitting a duplicate.
                if (await _emailAvailability.IsRegisteredAsync(probeEmail, AvailabilityLookupMode.Advisory))
                    throw new ConflictException($"An account is already registered with the email: {email}");
                if (await _usernameAvailability.IsUnavailableAsync(username, DateTime.UtcNow))
                    throw new UsernameTakenException(username);

                userType = AuthRoles.NormalizeOrThrow(userType);
                string hashedPassword = HashPassword(password);

                User user = new User
                {
                    Email = email,
                    Username = username,
                    // Carried through the verification token, or the casing typed here is lost by
                    // the time the account is actually inserted on the verify step.
                    UsernameDisplay = usernameForms.Display,
                    Password = hashedPassword,
                    Usertype = userType
                };

                var artifacts = await _tokenService.GenerateVerificationArtifactsAsync(
                    user,
                    VerificationPurpose.SignUp
                );
                await _authNotificationService.SendSignupVerificationAsync(
                    email,
                    artifacts.LinkToken,
                    artifacts.OtpChallenge.Code
                );

                return artifacts.OtpChallenge;
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[InternalServerErrorException] SignUpAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<UserToken> VerifyAsync(string token, SessionTransport transport)
        {
            try
            {
                var user = await _tokenService.VerifyVerificationToken(
                    token,
                    VerificationPurpose.SignUp
                );

                user.Email = EmailPolicy.Sanitize(user.Email);
                var probeEmail = EmailPolicy.Normalize(user.Email);
                // Authoritative: CreateUserAsync is a few lines away, so a stale "absent" here
                // would turn a clean 409 into a unique-index violation surfacing as a 500.
                if (await _emailAvailability.IsRegisteredAsync(probeEmail))
                    throw new ConflictException($"An account is already registered with the email: {user.Email}");
                if (string.IsNullOrWhiteSpace(user.Username))
                    throw new BadRequestException("A username is required to complete signup.");
                // Prefer the display form off the token: it normalises to the same key and still
                // carries the casing. A token minted before this column existed has none, and falls
                // back to the username, which is exactly the pre-existing behaviour.
                var verifiedUsername = UsernamePolicy.NormalizeAndValidateWithDisplay(
                    string.IsNullOrWhiteSpace(user.UsernameDisplay) ? user.Username : user.UsernameDisplay);
                user.Username = verifiedUsername.Username;
                user.UsernameDisplay = verifiedUsername.Display;
                if (await _usernameAvailability.IsUnavailableAsync(user.Username, DateTime.UtcNow))
                    throw new UsernameTakenException(user.Username);

                await _userRepository.CreateUserAsync(user);
                await _usernameAvailability.MarkTakenAsync(user.Username);
                await _emailAvailability.MarkRegisteredAsync(probeEmail);

                return await _authSessionService.IssueAsync(user, transport);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] VerifyAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<UserToken> VerifyOtpAsync(
            string code,
            string challenge,
            SessionTransport transport
        )
        {
            try
            {
                var user = await _tokenService.VerifyVerificationOtpAsync(
                    code,
                    challenge,
                    VerificationPurpose.SignUp
                );

                user.Email = EmailPolicy.Sanitize(user.Email);
                var probeEmail = EmailPolicy.Normalize(user.Email);
                // Authoritative: CreateUserAsync is a few lines away, so a stale "absent" here
                // would turn a clean 409 into a unique-index violation surfacing as a 500.
                if (await _emailAvailability.IsRegisteredAsync(probeEmail))
                    throw new ConflictException($"An account is already registered with the email: {user.Email}");
                if (string.IsNullOrWhiteSpace(user.Username))
                    throw new BadRequestException("A username is required to complete signup.");
                // Prefer the display form off the token: it normalises to the same key and still
                // carries the casing. A token minted before this column existed has none, and falls
                // back to the username, which is exactly the pre-existing behaviour.
                var verifiedUsername = UsernamePolicy.NormalizeAndValidateWithDisplay(
                    string.IsNullOrWhiteSpace(user.UsernameDisplay) ? user.Username : user.UsernameDisplay);
                user.Username = verifiedUsername.Username;
                user.UsernameDisplay = verifiedUsername.Display;
                if (await _usernameAvailability.IsUnavailableAsync(user.Username, DateTime.UtcNow))
                    throw new UsernameTakenException(user.Username);

                await _userRepository.CreateUserAsync(user);
                await _usernameAvailability.MarkTakenAsync(user.Username);
                await _emailAvailability.MarkRegisteredAsync(probeEmail);

                return await _authSessionService.IssueAsync(user, transport);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] VerifyOtpAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<VerificationOtpChallenge> RecoverPasswordAsync(string username)
        {
            try
            {
                var normalizedUsername = UsernamePolicy.Normalize(username);
                var existingUser = string.IsNullOrEmpty(normalizedUsername)
                    ? null
                    : await _userRepository.GetRecoveryByUsernameAsync(normalizedUsername);
                if (existingUser == null || existingUser.IsDisabled)
                    return BuildPlaceholderRecoveryChallenge();

                if (!existingUser.HasLocalPassword)
                {
                    if (existingUser.SignInProviders.Count > 0)
                    {
                        await _authNotificationService.SendProviderSignInReminderAsync(
                            existingUser.Email,
                            existingUser.SignInProviders,
                            existingUser.RecipientName
                        );
                    }

                    return BuildPlaceholderRecoveryChallenge();
                }

                User user = new User
                {
                    Email = existingUser.Email,
                    Password = "placeholder",
                    Usertype = "placeholder"
                };

                var artifacts = await _tokenService.GenerateVerificationArtifactsAsync(
                    user,
                    VerificationPurpose.ResetPassword,
                    replaceExisting: true
                );
                await _authNotificationService.SendPasswordResetAsync(
                    existingUser.Email,
                    artifacts.LinkToken,
                    artifacts.OtpChallenge.Code,
                    existingUser.RecipientName
                );

                return artifacts.OtpChallenge;
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] RecoverPasswordAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task RecoverUsernameAsync(string email)
        {
            try
            {
                // Normalising matters more than it looks: the column is citext, so the old
                // Trim() alone was enough for the database, but the filter hashes the literal
                // string and would miss a mixed-case address.
                var normalizedEmail = EmailPolicy.Normalize(email);

                // Skips the query outright for an address no account has ever used, which is what
                // most probes against this endpoint are. Deliberately the filter-only check rather
                // than IsRegisteredAsync: the latter would fall back to its own query whenever the
                // filter cannot answer, leaving this path doing two round trips instead of one.
                // Advisory, and safely so — the method reports nothing to the caller either way,
                // so a filter lagging a very recent signup costs one unsent reminder.
                if (_emailAvailability.IsDefinitelyUnregistered(normalizedEmail))
                    return;

                var existingUser = await _userRepository.GetRecoveryByEmailAsync(normalizedEmail);
                if (existingUser == null || existingUser.IsDisabled)
                    return;

                if (existingUser.HasLocalPassword
                    && !string.IsNullOrWhiteSpace(existingUser.Username))
                {
                    await _authNotificationService.SendUsernameReminderAsync(
                        existingUser.Email,
                        existingUser.Username,
                        existingUser.RecipientName
                    );
                    return;
                }

                if (existingUser.SignInProviders.Count > 0)
                {
                    await _authNotificationService.SendProviderSignInReminderAsync(
                        existingUser.Email,
                        existingUser.SignInProviders,
                        existingUser.RecipientName
                    );
                }
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] RecoverUsernameAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task ResetPasswordAsync(string token, string password)
        {
            try
            {
                var user = await _tokenService.VerifyVerificationToken(
                    token,
                    VerificationPurpose.ResetPassword
                );
                await ChangePasswordInternalAsync(user.Email, password);
                await SendPasswordChangedBestEffortAsync(user.Email);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] ResetPasswordAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task ResetPasswordWithOtpAsync(string code, string challenge, string password)
        {
            try
            {
                var user = await _tokenService.VerifyVerificationOtpAsync(
                    code,
                    challenge,
                    VerificationPurpose.ResetPassword
                );
                await ChangePasswordInternalAsync(user.Email, password);
                await SendPasswordChangedBestEffortAsync(user.Email);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] ResetPasswordWithOtpAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task ChangePasswordForAuthenticatedUserAsync(string email, string currentPassword, string newPassword)
        {
            try
            {
                var existingUser = await _userRepository.GetAuthByEmailAsync(email)
                    ?? throw new UnauthorizedException("User not found.");

                await EnsureUserEnabledAsync(ToUser(existingUser));

                if (existingUser.Password == null)
                    throw new BadRequestException(
                        "This account uses social login and has no password set."
                    );

                bool isValid = VerifyPassword(currentPassword, existingUser.Password);
                if (!isValid)
                    throw new UnauthorizedException("Current password is incorrect.");

                if (currentPassword == newPassword)
                    throw new BadRequestException(
                        "New password must be different from your current password."
                    );

                await ChangePasswordInternalAsync(email, newPassword);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] ChangePasswordForAuthenticatedUserAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<OAuthAuthenticationResult> GoogleAsync(
            string token,
            SessionTransport transport,
            string? expectedNonce = null,
            string? returnUrl = null
        )
        {
            try
            {
                OAuthUser oauthUser = await _oauthService.VerifyGoogleTokenAsync(
                    token,
                    expectedNonce
                );
                if (oauthUser == null)
                    throw new UnauthorizedException("Invalid Google Token");

                var user = await ResolveGoogleUserAsync(oauthUser);
                if (user == null)
                {
                    return OAuthAuthenticationResult.RoleSelectionRequired(
                        await CreatePendingOAuthSignupAsync(oauthUser, transport)
                    );
                }

                await EnsureUserEnabledAsync(user);
                user = await EnsureOAuthRoleAsync(user);

                return await ResolvePostAuthOutcomeAsync(
                    user, transport, rememberMe: false, returnUrl,
                    OAuthAuthenticationResult.Authenticated,
                    OAuthAuthenticationResult.RequiresStepUp
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] GoogleAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<OAuthAuthenticationResult> GoogleCodeAsync(
            string code,
            string codeVerifier,
            string redirectUri,
            SessionTransport transport,
            string? nonce = null,
            string? returnUrl = null
        )
        {
            try
            {
                var idToken = await _oauthService.ExchangeGoogleCodeAsync(code, codeVerifier, redirectUri);
                return await GoogleAsync(idToken, transport, nonce, returnUrl);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] GoogleCodeAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<OAuthAuthenticationResult> MicrosoftAsync(
            string token,
            SessionTransport transport,
            string? expectedNonce = null,
            string? returnUrl = null
        )
        {
            try
            {
                OAuthUser oauthUser = await _oauthService.VerifyMicrosoftTokenAsync(
                    token,
                    expectedNonce
                );
                if (oauthUser == null)
                    throw new UnauthorizedException("Invalid Microsoft Token");

                var user = await ResolveMicrosoftUserAsync(oauthUser);
                if (user == null)
                {
                    return OAuthAuthenticationResult.RoleSelectionRequired(
                        await CreatePendingOAuthSignupAsync(oauthUser, transport)
                    );
                }

                await EnsureUserEnabledAsync(user);
                user = await EnsureOAuthRoleAsync(user);

                return await ResolvePostAuthOutcomeAsync(
                    user, transport, rememberMe: false, returnUrl,
                    OAuthAuthenticationResult.Authenticated,
                    OAuthAuthenticationResult.RequiresStepUp
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] MicrosoftAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<UserToken> CompleteOAuthSignupAsync(
            string signupToken,
            string usertype,
            string? username,
            SessionTransport transport
        )
        {
            try
            {
                usertype = AuthRoles.NormalizeOrThrow(usertype);
                var pending = await GetPendingOAuthSignupAsync(signupToken)
                    ?? throw new UnauthorizedException(
                        "OAuth signup session is invalid or expired."
                    );

                if (pending.Transport != transport)
                    throw new UnauthorizedException("OAuth signup transport mismatch.");

                var oauthUser = new OAuthUser(
                    pending.ProviderUserId,
                    pending.Email,
                    pending.Name,
                    pending.Provider
                );
                var user = pending.Provider switch
                {
                    "google" => await ResolveGoogleUserAsync(oauthUser),
                    "microsoft" => await ResolveMicrosoftUserAsync(oauthUser),
                    _ => throw new BadRequestException("Unsupported OAuth provider.")
                };

                if (user == null)
                {
                    // Same shape as VerifyAsync: validate, check authoritatively because the insert
                    // is a few lines away, then create and record both names in the filters.
                    var oauthUsername = UsernamePolicy.NormalizeAndValidateWithDisplay(username);
                    var newUsername = oauthUsername.Username;
                    if (await _usernameAvailability.IsUnavailableAsync(newUsername, DateTime.UtcNow))
                        throw new UsernameTakenException(newUsername);

                    user = await _userRepository.CreateUserAsync(new User
                    {
                        Email = pending.Email,
                        Username = newUsername,
                        UsernameDisplay = oauthUsername.Display,
                        Usertype = usertype,
                        GoogleID = pending.Provider == "google" ? pending.ProviderUserId : null,
                        MicrosoftID = pending.Provider == "microsoft"
                            ? pending.ProviderUserId
                            : null,
                    });
                    // Without these writes, the name and address that just signed up through a
                    // provider would keep reporting as free until the next scheduled rebuild.
                    await _usernameAvailability.MarkTakenAsync(newUsername);
                    await _emailAvailability.MarkRegisteredAsync(EmailPolicy.Normalize(user.Email));
                }
                else
                {
                    // Any username supplied is deliberately ignored here. The provider account
                    // resolved to one that already exists, and renaming it from an unauthenticated
                    // signup token would bypass the MFA gate and cooldown on PATCH /profile/username.
                    await EnsureUserEnabledAsync(user);
                    user = await EnsureOAuthRoleAsync(user);
                }

                await _cacheService.DeleteKeyAsync(PendingOAuthSignupKey(signupToken));
                return await _authSessionService.IssueAsync(user, transport);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] CompleteOAuthSignupAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<User> GetCurrentUserAsync(int userId)
        {
            try
            {
                var user = await _userRepository.GetUserAsync(userId)
                    ?? throw new ResourceNotFoundException($"User with ID {userId} is not found");

                await EnsureUserEnabledAsync(user);
                return user;
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] GetCurrentUserAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<UserToken> HandleTokensAsync(
            string oldRefreshToken,
            string? sessionBindingToken,
            SessionTransport transport
        )
        {
            try
            {
                var validation = await _tokenService.ValidateRefreshToken(
                    oldRefreshToken,
                    sessionBindingToken,
                    transport,
                    _requestInfo
                );
                var user = await _userRepository.GetUserAsync(validation.UserId);
                if (user == null)
                    throw new ResourceNotFoundException($"User with ID {validation.UserId} is not found");
                await EnsureUserEnabledAsync(user, revokeSessions: true);

                return await _authSessionService.IssueAsync(
                    user,
                    validation.Transport,
                    validation.SessionId
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] HandleTokensAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<AuthenticatedSessionResult> VerifyDeviceLoginAsync(
            string token,
            SessionTransport transport
        )
        {
            try
            {
                return await _deviceService.VerifyDeviceAsync(token, transport);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] VerifyDeviceLoginAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<StartLoginStepUpResponse> StartLoginStepUpAsync(string challenge, string method)
        {
            try
            {
                return await _loginStepUpChallengeService.StartAsync(challenge, method);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] StartLoginStepUpAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<AuthenticatedSessionResult> VerifyLoginStepUpAsync(string challenge, string code)
        {
            try
            {
                return await _loginStepUpChallengeService.VerifySmsAsync(challenge, code);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] VerifyLoginStepUpAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task<AuthenticatedSessionResult> VerifyTotpLoginStepUpAsync(string challenge, string code)
        {
            try
            {
                return await _loginStepUpChallengeService.VerifyTotpAsync(challenge, code);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] VerifyTotpLoginStepUpAsync failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        public async Task HandleLogoutAsync(
            string refreshToken,
            string? sessionBindingToken,
            SessionTransport transport
        )
        {
            try
            {
                var validation = await _tokenService.ValidateRefreshToken(
                    refreshToken,
                    sessionBindingToken,
                    transport,
                    _requestInfo
                );
                await _tokenService.RevokeRefreshSessionAsync(validation.SessionId);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] HashPassword failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        private string HashPassword(string password)
        {
            try
            {
                return BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] HashPassword failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        private bool VerifyPassword(string password, string hashedPassword)
        {
            try
            {
                return BCrypt.Net.BCrypt.Verify(password, hashedPassword);
            }
            catch (Exception e)
            {
                if (e is AppException)
                    throw;

                Logger.Error($"[AuthService] VerifyPassword failed: {e}");
                throw new InternalServerErrorException();
            }
        }

        private async Task ChangePasswordInternalAsync(string email, string password)
        {
            var hashedPassword = HashPassword(password);

            var existingUser = await _userRepository.GetAuthByEmailAsync(email)
                ?? throw new UnauthorizedException("Invalid token");
            await EnsureUserEnabledAsync(ToUser(existingUser));

            await _userRepository.UpdateUserAsync(existingUser.Id, new User
            {
                Email = existingUser.Email,
                Password = hashedPassword,
                Usertype = existingUser.Usertype,
            });
            await _userRepository.IncrementAuthVersionAsync(existingUser.Id);
            await _tokenService.RevokeAllRefreshSessionsAsync(existingUser.Id);
        }

        private async Task SendPasswordChangedBestEffortAsync(string email)
        {
            try
            {
                var user = await _userRepository.GetRecoveryByEmailAsync(email);
                await _authNotificationService.SendPasswordChangedAsync(
                    email,
                    user?.RecipientName
                );
            }
            catch (Exception e)
            {
                Logger.Error($"[AuthService] Password changed notification failed: {e}");
            }
        }

        private async Task<User?> ResolveGoogleUserAsync(OAuthUser oauthUser)
        {
            // The provider id is the authoritative identity for a provider sign-in, so it is
            // resolved first and on its own. An account that changes its email address keeps the
            // address the provider still reports, and once that released address is claimed by
            // someone else the two lookups disagree permanently — comparing them here would lock
            // the original account out of the only sign-in method it may have.
            var providerUser = await _userRepository.GetOAuthByGoogleIdAsync(oauthUser.Id);
            if (providerUser != null)
                return ToUser(providerUser);

            var emailUser = await _userRepository.GetOAuthByEmailAsync(oauthUser.Email);
            if (emailUser == null)
                return null;

            if (string.IsNullOrWhiteSpace(emailUser.GoogleID))
            {
                emailUser = await _userRepository.UpdateProviderIdsAsync(
                    emailUser.Id,
                    oauthUser.Id,
                    null
                ) ?? emailUser;
            }

            return ToUser(emailUser);
        }

        private async Task<User?> ResolveMicrosoftUserAsync(OAuthUser oauthUser)
        {
            // The provider id is the authoritative identity for a provider sign-in, so it is
            // resolved first and on its own. An account that changes its email address keeps the
            // address the provider still reports, and once that released address is claimed by
            // someone else the two lookups disagree permanently — comparing them here would lock
            // the original account out of the only sign-in method it may have.
            var providerUser = await _userRepository.GetOAuthByMicrosoftIdAsync(oauthUser.Id);
            if (providerUser != null)
                return ToUser(providerUser);

            var emailUser = await _userRepository.GetOAuthByEmailAsync(oauthUser.Email);
            if (emailUser == null)
                return null;

            if (string.IsNullOrWhiteSpace(emailUser.MicrosoftID))
            {
                emailUser = await _userRepository.UpdateProviderIdsAsync(
                    emailUser.Id,
                    null,
                    oauthUser.Id
                ) ?? emailUser;
            }

            return ToUser(emailUser);
        }

        private async Task<User> EnsureOAuthRoleAsync(User user)
        {
            user.Usertype = AuthRoles.NormalizeStored(user.Usertype);

            if (AuthRoles.IsKnownRole(user.Usertype))
                return user;

            var updatedUser = await _userRepository.UpdateUserAsync(user.Id, new User
            {
                Email = user.Email,
                Usertype = AuthRoles.DefaultOAuthRole,
            });

            if (updatedUser != null)
                return updatedUser;

            user.Usertype = AuthRoles.DefaultOAuthRole;
            return user;
        }

        private async Task<PendingOAuthSignupChallenge> CreatePendingOAuthSignupAsync(
            OAuthUser oauthUser,
            SessionTransport transport
        )
        {
            var signupToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var pendingState = new PendingOAuthSignupState
            {
                ProviderUserId = oauthUser.Id,
                Email = oauthUser.Email,
                Name = oauthUser.Name,
                Provider = oauthUser.Provider,
                Transport = transport,
            };

            var stored = await _cacheService.SetValueAsync(
                PendingOAuthSignupKey(signupToken),
                JsonConvert.SerializeObject(pendingState),
                PendingOAuthSignupTtl
            );

            if (!stored)
                throw new NotAvailableException();

            return new PendingOAuthSignupChallenge
            {
                SignupToken = signupToken,
                Email = oauthUser.Email,
                Name = oauthUser.Name,
                Provider = oauthUser.Provider,
            };
        }

        private async Task<PendingOAuthSignupState?> GetPendingOAuthSignupAsync(string signupToken)
        {
            var json = await _cacheService.GetValueAsync(PendingOAuthSignupKey(signupToken));
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonConvert.DeserializeObject<PendingOAuthSignupState>(json);
        }

        private static string PendingOAuthSignupKey(string signupToken) =>
            $"oauth:pending:{signupToken}";

        private async Task<T> ResolvePostAuthOutcomeAsync<T>(
            User user,
            SessionTransport transport,
            bool rememberMe,
            string? returnUrl,
            Func<AuthenticatedSessionResult, T> onAuthenticated,
            Func<LoginStepUpChallengeResponse, T> onStepUp
        )
        {
            // Dev/test-only: seed accounts skip the MFA step-up challenge entirely.
            if (_seedBypass.IsBypassEnabledFor(user.Email))
            {
                return onAuthenticated(new AuthenticatedSessionResult
                {
                    UserToken = await _authSessionService.IssueAsync(user, transport, rememberMe: rememberMe)
                });
            }

            if (await _deviceTrustService.IsTrustedAsync(user.Id, _requestInfo))
            {
                return onAuthenticated(new AuthenticatedSessionResult
                {
                    UserToken = await _authSessionService.IssueAsync(user, transport, rememberMe: rememberMe)
                });
            }

            var shouldRequireTotpStepUp = false;
            if (EnvironmentSetting.AuthTotpMfaStepUpEnabled)
            {
                shouldRequireTotpStepUp = (await _totpMfaEnrollmentService.GetEnrollmentAsync(user.Id))?.IsTotpMfaEnabled == true;
            }

            if (!EnvironmentSetting.AuthSmsMfaEnforcementEnabled && !shouldRequireTotpStepUp)
            {
                await _deviceService.EnsureDeviceKnownAsync(user.Id, user.Email, _requestInfo, returnUrl);
                return onAuthenticated(new AuthenticatedSessionResult
                {
                    UserToken = await _authSessionService.IssueAsync(user, transport, rememberMe: rememberMe)
                });
            }

            var stepUp = await _loginStepUpChallengeService.CreateChallengeAsync(user, transport, rememberMe, returnUrl);
            return onStepUp(stepUp);
        }

        private async Task EnsureUserEnabledAsync(User user, bool revokeSessions = false)
        {
            if (!user.IsDisabled)
                return;

            if (revokeSessions)
                await _tokenService.RevokeAllRefreshSessionsAsync(user.Id);

            throw new ForbiddenException("This account is disabled.");
        }

        private static User ToUser(UserAuthRecord record)
        {
            return new User
            {
                Id = record.Id,
                Email = record.Email,
                Password = record.Password,
                Usertype = AuthRoles.NormalizeStored(record.Usertype),
                IsDisabled = record.IsDisabled,
                AuthVersion = record.AuthVersion,
            };
        }

        private static User ToUser(UserOAuthRecord record)
        {
            return new User
            {
                Id = record.Id,
                Email = record.Email,
                Usertype = AuthRoles.NormalizeStored(record.Usertype),
                GoogleID = record.GoogleID,
                MicrosoftID = record.MicrosoftID,
                IsDisabled = record.IsDisabled,
                AuthVersion = record.AuthVersion,
            };
        }

        private static VerificationOtpChallenge BuildPlaceholderRecoveryChallenge()
        {
            return new VerificationOtpChallenge
            {
                Code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"),
                Challenge = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30),
            };
        }

        private sealed class PendingOAuthSignupState
        {
            public required string ProviderUserId
            {
                get; set;
            }
            public required string Email
            {
                get; set;
            }
            public required string Name
            {
                get; set;
            }
            public required string Provider
            {
                get; set;
            }
            public SessionTransport Transport
            {
                get; set;
            }
        }
    }
}
