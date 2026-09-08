using backend.main.application.bootstrap;
using backend.main.application.features;
using backend.main.application.security;
using backend.main.features.auth;
using backend.main.features.auth.captcha;
using backend.main.features.auth.contracts.requests;
using backend.main.features.auth.contracts.responses;
using backend.main.features.auth.oauth;
using backend.main.features.auth.token;
using backend.main.features.profile;
using backend.main.features.profile.email;
using backend.main.shared.exceptions.http;
using backend.main.shared.requests;
using backend.main.shared.responses;
using backend.main.shared.utilities.logger;
using backend.main.utilities;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace backend.main.features.auth
{
    /// <summary>
    /// Authentication, session, verification, and password-recovery endpoints.
    /// </summary>
    [ApiController]
    [FeatureGate(FeatureFlagKeys.Auth)]
    [Route(RoutePaths.AuthPrefix)]
    public class AuthController : ControllerBase
    {
        private const string DefaultFrontendUrl = "http://localhost:3090";
        private readonly IAuthService _authService;
        private readonly IUsernameAvailabilityService _usernameAvailability;
        private readonly IEmailAvailabilityService _emailAvailability;
        private readonly IAntiforgery _antiforgery;
        private readonly ICaptchaService _captchaService;
        private readonly SeedAccountBypassPolicy _seedBypass;
        private readonly ClientRequestInfo _requestInfo;
        private readonly IConfiguration _configuration;
        private readonly ITokenService _tokenService;
        private readonly IEmailChangeService _emailChangeService;

        public AuthController(
            IAuthService authService,
            IUsernameAvailabilityService usernameAvailability,
            IEmailAvailabilityService emailAvailability,
            IAntiforgery antiforgery,
            ICaptchaService captchaService,
            SeedAccountBypassPolicy seedBypass,
            ClientRequestInfo requestInfo,
            IConfiguration configuration,
            ITokenService tokenService,
            IEmailChangeService emailChangeService
        )
        {
            _authService = authService;
            _usernameAvailability = usernameAvailability;
            _emailAvailability = emailAvailability;
            _antiforgery = antiforgery;
            _captchaService = captchaService;
            _seedBypass = seedBypass;
            _requestInfo = requestInfo;
            _configuration = configuration;
            _tokenService = tokenService;
            _emailChangeService = emailChangeService;
        }

        [HttpPost("login")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<LoginAuthenticationResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> LocalAuthenticate([FromBody] LoginRequest request)
        {
            try
            {
                if (!_seedBypass.IsBypassEnabledForUsername(request.Username)
                    && !await _captchaService.VerifyCaptchaAsync(request.Captcha))
                    throw new BadRequestException("Invalid captcha.");

                var result = await _authService.LoginAsync(
                    request.Username,
                    request.Password,
                    SessionTransportResolver.ResolveOrDefault(request.Transport),
                    request.RememberMe,
                    request.ReturnUrl
                );

                var response = CreateLoginAuthenticationResponse(result);
                return StatusCode(
                    200,
                    new ApiResponse<LoginAuthenticationResponse>(ResolveLoginMessage(response.Type), response)
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] LocalAuthenticate failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("signup")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<VerificationChallengeResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> LocalSignup([FromBody] SignUpRequest request)
        {
            try
            {
                if (!_seedBypass.IsBypassEnabledFor(request.Email)
                    && !await _captchaService.VerifyCaptchaAsync(request.Captcha))
                    throw new BadRequestException("Invalid captcha.");

                var challenge = await _authService.SignUpAsync(
                    request.Email,
                    request.Username,
                    request.Password,
                    request.Usertype
                );

                return StatusCode(
                    200,
                    new ApiResponse<VerificationChallengeResponse>(
                        "Verification email sent.",
                        new VerificationChallengeResponse
                        {
                            Challenge = challenge.Challenge,
                            ExpiresAtUtc = challenge.ExpiresAtUtc,
                        }
                    )
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] LocalSignup failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        /// <summary>
        /// Reports whether a username is free, so signup can tell the user before they submit.
        /// </summary>
        /// <remarks>
        /// Anonymous by necessity — it serves the signup form, where there is no session yet — and
        /// therefore an enumeration surface, which is why it carries its own rate-limit policy
        /// rather than the global one. It returns nothing beyond a boolean; a caller learns only
        /// what they would learn by attempting to sign up.
        ///
        /// The answer is advisory. It is not a reservation, and a name reported free can be taken
        /// a moment later; the unique index is what actually decides.
        /// </remarks>
        [HttpGet("username/availability")]
        [AllowAnonymous]
        [EnableRateLimiting(RateLimiterConfiguration.UsernameAvailabilityPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<UsernameAvailabilityResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> CheckUsernameAvailability(
            [FromQuery] string username,
            CancellationToken cancellationToken)
        {
            try
            {
                var normalized = UsernamePolicy.NormalizeAndValidate(username);

                // Advisory: this endpoint only reports, it never claims, so letting the filter
                // answer outright is what makes a type-ahead probe cheap. The signup path that
                // actually takes the name still confirms against the database.
                var unavailable = await _usernameAvailability.IsUnavailableAsync(
                    normalized,
                    DateTime.UtcNow,
                    AvailabilityLookupMode.Advisory,
                    cancellationToken
                );

                return StatusCode(
                    200,
                    new ApiResponse<UsernameAvailabilityResponse>(
                        unavailable ? "Username is taken." : "Username is available.",
                        new UsernameAvailabilityResponse
                        {
                            Username = normalized,
                            Available = !unavailable,
                        }
                    )
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] CheckUsernameAvailability failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        /// <summary>
        /// Reports whether an email address is free, so signup can point a returning user at login
        /// instead of letting them fill in the whole form and fail.
        /// </summary>
        /// <remarks>
        /// Anonymous by necessity — it serves the signup form, where there is no session yet — and
        /// therefore an account-existence oracle, accepted deliberately because the signup UX is
        /// judged to be worth it.
        ///
        /// Be clear about what that costs: this is strictly cheaper to script than the signup it
        /// serves. <c>LocalSignup</c> requires an antiforgery token and a passing captcha; this
        /// requires neither and answers with a boolean. So existence testing that was previously
        /// behind a captcha is now bounded only by
        /// <see cref="RateLimiterConfiguration.EmailAvailabilityPolicyName"/>, which is why that
        /// policy is half the username budget. Gate this endpoint, or lower that limit, if
        /// enumeration ever matters more than the type-ahead. See the bloom filter section of
        /// docs/CONFIGURATION.md.
        ///
        /// The answer is advisory. An address reported free can be registered a moment later; the
        /// unique index is what actually decides.
        /// </remarks>
        [HttpGet("email/availability")]
        [AllowAnonymous]
        [EnableRateLimiting(RateLimiterConfiguration.EmailAvailabilityPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<EmailAvailabilityResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> CheckEmailAvailability(
            [FromQuery] string email,
            CancellationToken cancellationToken)
        {
            try
            {
                var normalized = EmailPolicy.NormalizeAndValidate(email);

                // Advisory: this endpoint only reports, it never claims, so letting the filter
                // answer outright is what makes a type-ahead probe cheap. The signup path that
                // actually creates the account still confirms against the database.
                var registered = await _emailAvailability.IsRegisteredAsync(
                    normalized,
                    AvailabilityLookupMode.Advisory,
                    cancellationToken
                );

                return StatusCode(
                    200,
                    new ApiResponse<EmailAvailabilityResponse>(
                        registered ? "Email is already registered." : "Email is available.",
                        new EmailAvailabilityResponse
                        {
                            Email = normalized,
                            Available = !registered,
                        }
                    )
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] CheckEmailAvailability failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpGet("verify/email-change")]
        [AllowAnonymous]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status302Found)]
        public IActionResult VerifyEmailChange([FromQuery] string token)
        {
            var redirectUrl = BuildFrontendAuthUrl("verify-email-change", token);
            if (redirectUrl != null)
                return Redirect(redirectUrl);

            return Ok(
                new MessageResponse(
                    "Confirming an email change requires confirmation from the frontend. Open the link in the app to complete the change."
                )
            );
        }

        /// <summary>
        /// Applies a pending email change. Anonymous by design: the token and the OTP challenge are
        /// each bound to an account id and single-use, which is what lets the emailed link be opened
        /// on whatever device reads that inbox. No session is issued - confirming signs every
        /// session out, including the one that asked.
        /// </summary>
        [HttpPost("verify/email-change")]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status409Conflict)]
        public async Task<IActionResult> VerifyEmailChange(
            [FromBody] EmailChangeConfirmationRequest request)
        {
            try
            {
                PendingEmailChange pending;

                if (!string.IsNullOrWhiteSpace(request.Token))
                {
                    pending = await _tokenService.ConsumeEmailChangeTokenAsync(request.Token);
                }
                else if (!string.IsNullOrWhiteSpace(request.Code)
                    && !string.IsNullOrWhiteSpace(request.Challenge))
                {
                    pending = await _tokenService.ConsumeEmailChangeOtpAsync(
                        request.Code,
                        request.Challenge
                    );
                }
                else
                {
                    throw new BadRequestException(
                        "Provide either a confirmation token or a code and challenge."
                    );
                }

                await _emailChangeService.ConfirmAsync(pending, HttpContext.RequestAborted);

                // Every session was just revoked, so there is nothing to clear server-side but the
                // browser's own refresh cookie.
                HttpUtility.ClearBrowserRefreshSession(Response);

                return Ok(new MessageResponse(
                    "Email changed successfully. Please sign in with your new email address."
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] VerifyEmailChange failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("verify/otp")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<AuthenticatedSessionResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> LocalVerifyOtp([FromBody] OtpVerificationRequest request)
        {
            try
            {
                var userToken = await _authService.VerifyOtpAsync(
                    request.Code,
                    request.Challenge,
                    SessionTransportResolver.ResolveOrDefault(request.Transport)
                );

                return StatusCode(
                    200,
                    new ApiResponse<AuthenticatedSessionResponse>(
                        "Verification successful",
                        CreateSessionResponse(userToken.user, userToken.token)
                    )
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] LocalVerifyOtp failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpGet("verify")]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status302Found)]
        public IActionResult LocalVerify([FromQuery] string token)
        {
            var redirectUrl = BuildFrontendAuthUrl("verify", token);
            if (redirectUrl != null)
                return Redirect(redirectUrl);

            return Ok(
                new MessageResponse(
                    "Email verification requires confirmation from the frontend. Open the verification link in the app and confirm to complete verification."
                )
            );
        }

        [HttpPost("verify")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<AuthenticatedSessionResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> LocalVerify([FromBody] VerificationTokenRequest request)
        {
            try
            {
                var userToken = await _authService.VerifyAsync(
                    request.Token,
                    SessionTransportResolver.ResolveOrDefault(request.Transport)
                );

                return StatusCode(
                    200,
                    new ApiResponse<AuthenticatedSessionResponse>(
                        "Verification successful",
                        CreateSessionResponse(userToken.user, userToken.token)
                    )
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] LocalVerify POST failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("google")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<OAuthAuthenticationResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GoogleAuthenticate([FromBody] GoogleRequest request)
        {
            try
            {
                var result = await _authService.GoogleAsync(
                    request.Token,
                    SessionTransportResolver.ResolveOrDefault(request.Transport),
                    request.Nonce,
                    request.ReturnUrl
                );
                var response = CreateOAuthAuthenticationResponse(result);

                return StatusCode(
                    200,
                    new ApiResponse<OAuthAuthenticationResponse>(ResolveOAuthMessage(response.Type), response)
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] GoogleAuthenticate failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("google/code")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<OAuthAuthenticationResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GoogleCodeAuthenticate([FromBody] GoogleCodeRequest request)
        {
            try
            {
                var result = await _authService.GoogleCodeAsync(
                    request.Code,
                    request.CodeVerifier,
                    request.RedirectUri,
                    SessionTransportResolver.ResolveOrDefault(request.Transport),
                    request.Nonce,
                    request.ReturnUrl
                );
                var response = CreateOAuthAuthenticationResponse(result);

                return StatusCode(
                    200,
                    new ApiResponse<OAuthAuthenticationResponse>(ResolveOAuthMessage(response.Type), response)
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] GoogleCodeAuthenticate failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("microsoft")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<OAuthAuthenticationResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> MicrosoftAuthenticate([FromBody] MicrosoftRequest request)
        {
            try
            {
                var result = await _authService.MicrosoftAsync(
                    request.Token,
                    SessionTransportResolver.ResolveOrDefault(request.Transport),
                    request.Nonce,
                    request.ReturnUrl
                );
                var response = CreateOAuthAuthenticationResponse(result);

                return StatusCode(
                    200,
                    new ApiResponse<OAuthAuthenticationResponse>(ResolveOAuthMessage(response.Type), response)
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] ChangePassword failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [Authorize]
        [HttpGet("me")]
        [ProducesResponseType(typeof(ApiResponse<CurrentUserResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Me()
        {
            try
            {
                var userPayload = User.GetUserPayload();
                var user = await _authService.GetCurrentUserAsync(userPayload.Id);

                return StatusCode(
                    200,
                    new ApiResponse<CurrentUserResponse>(
                        "Current user fetched successfully.",
                        CreateCurrentUserResponse(user)
                    )
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] Me failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("refresh")]
        [ValidateAntiForgeryToken]
        [ProducesResponseType(typeof(ApiResponse<AuthenticatedSessionResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest? request)
        {
            try
            {
                string? refreshToken = HttpUtility.ResolveBrowserRefreshToken(Request);
                string? sessionBindingToken = HttpUtility.ResolveBrowserSessionBindingToken(Request);
                if (string.IsNullOrEmpty(refreshToken))
                    throw new UnauthorizedException("Missing refresh token");

                var userToken = await _authService.HandleTokensAsync(
                    refreshToken,
                    sessionBindingToken,
                    SessionTransport.BrowserCookie
                );

                return Ok(
                    new ApiResponse<AuthenticatedSessionResponse>(
                        "Session refreshed successfully.",
                        CreateSessionResponse(userToken.user, userToken.token)
                    )
                );
            }
            catch (Exception e)
            {
                HttpUtility.ClearBrowserRefreshSession(Response);

                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] Refresh failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("oauth/complete")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<AuthenticatedSessionResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> CompleteOAuthSignup([FromBody] CompleteOAuthSignupRequest request)
        {
            try
            {
                var userToken = await _authService.CompleteOAuthSignupAsync(
                    request.SignupToken,
                    request.Usertype,
                    request.Username,
                    SessionTransportResolver.ResolveOrDefault(request.Transport)
                );

                return StatusCode(
                    200,
                    new ApiResponse<AuthenticatedSessionResponse>(
                        "Signup completed successfully.",
                        CreateSessionResponse(userToken.user, userToken.token)
                    )
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] CompleteOAuthSignup failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("api/refresh")]
        [ProducesResponseType(typeof(ApiResponse<AuthenticatedSessionResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> ApiRefresh([FromBody] RefreshTokenRequest? request)
        {
            try
            {
                string? refreshToken = HttpUtility.ResolveApiRefreshToken(Request, request?.RefreshToken);
                string? sessionBindingToken = HttpUtility.ResolveApiSessionBindingToken(
                    Request,
                    request?.SessionBindingToken
                );
                if (string.IsNullOrEmpty(refreshToken))
                    throw new UnauthorizedException("Missing refresh token");

                var userToken = await _authService.HandleTokensAsync(
                    refreshToken,
                    sessionBindingToken,
                    SessionTransport.ApiToken
                );

                return Ok(
                    new ApiResponse<AuthenticatedSessionResponse>(
                        "Session refreshed successfully.",
                        CreateSessionResponse(userToken.user, userToken.token)
                    )
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] ApiRefresh failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpGet("csrf")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public IActionResult Csrf()
        {
            var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
            return Ok(
                new ApiResponse<object>(
                    "CSRF token fetched successfully.",
                    new
                    {
                        token = tokens.RequestToken
                    }
                )
            );
        }

        // CSRF is enforced by UseRefreshCsrfValidation (pre-auth) for this path. The MVC
        // [ValidateAntiForgeryToken] filter runs post-auth and would validate the antiforgery
        // token against the now-authenticated user, while the token/cookie the SPA holds are
        // bound to the anonymous session — an unsatisfiable contradiction that made authenticated
        // logout always fail. The middleware alone is the correct (and sufficient) guard here.
        [HttpPost("logout")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> Logout([FromBody] RefreshTokenRequest? request)
        {
            try
            {
                string? refreshToken = HttpUtility.ResolveBrowserRefreshToken(Request);
                string? sessionBindingToken = HttpUtility.ResolveBrowserSessionBindingToken(Request);
                if (string.IsNullOrEmpty(refreshToken))
                {
                    HttpUtility.ClearBrowserRefreshSession(Response);
                    return StatusCode(200, new MessageResponse("The user is already logged out."));
                }

                await _authService.HandleLogoutAsync(
                    refreshToken,
                    sessionBindingToken,
                    SessionTransport.BrowserCookie
                );
                HttpUtility.ClearBrowserRefreshSession(Response);

                return StatusCode(200, new MessageResponse("The user's logout is successful"));
            }
            catch (Exception e)
            {
                HttpUtility.ClearBrowserRefreshSession(Response);

                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] Logout failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("api/logout")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> ApiLogout([FromBody] RefreshTokenRequest? request)
        {
            try
            {
                string? refreshToken = HttpUtility.ResolveApiRefreshToken(Request, request?.RefreshToken);
                string? sessionBindingToken = HttpUtility.ResolveApiSessionBindingToken(
                    Request,
                    request?.SessionBindingToken
                );
                if (string.IsNullOrEmpty(refreshToken))
                    throw new UnauthorizedException("Missing refresh token");
                if (string.IsNullOrEmpty(sessionBindingToken))
                    throw new UnauthorizedException("Missing session binding token");

                await _authService.HandleLogoutAsync(
                    refreshToken,
                    sessionBindingToken,
                    SessionTransport.ApiToken
                );

                return StatusCode(200, new MessageResponse("The user's logout is successful"));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] ApiLogout failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpGet("device/verify")]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status302Found)]
        public IActionResult VerifyDevice([FromQuery] string token)
        {
            var redirectUrl = BuildFrontendAuthUrl("device/verify", token);
            if (redirectUrl != null)
                return Redirect(redirectUrl);

            return Ok(
                new MessageResponse(
                    "Open the verification link on the device you want to use and the frontend will finish signing you in automatically."
                )
            );
        }

        [HttpPost("device/verify")]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<AuthenticatedSessionResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> VerifyDevice([FromBody] VerificationTokenRequest request)
        {
            try
            {
                var result = await _authService.VerifyDeviceLoginAsync(
                    request.Token,
                    SessionTransportResolver.ResolveOrDefault(request.Transport)
                );

                return StatusCode(
                    200,
                    new ApiResponse<AuthenticatedSessionResponse>(
                        "Device verified. Login successful.",
                        CreateSessionResponse(result)
                    )
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] VerifyDevice POST failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("mfa/start")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<StartLoginStepUpResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> StartStepUp([FromBody] StartLoginStepUpRequest request)
        {
            try
            {
                var response = await _authService.StartLoginStepUpAsync(request.Challenge, request.Method);
                return Ok(new ApiResponse<StartLoginStepUpResponse>("Sign-in verification sent.", response));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] StartStepUp failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("mfa/verify")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<AuthenticatedSessionResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> VerifyStepUp([FromBody] VerifyLoginStepUpRequest request)
        {
            try
            {
                var response = await _authService.VerifyLoginStepUpAsync(request.Challenge, request.Code);
                return Ok(
                    new ApiResponse<AuthenticatedSessionResponse>(
                        "Sign-in verification successful.",
                        CreateSessionResponse(response)
                    )
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] VerifyStepUp failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("mfa/verify/totp")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<AuthenticatedSessionResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> VerifyTotpStepUp([FromBody] VerifyLoginStepUpRequest request)
        {
            try
            {
                var response = await _authService.VerifyTotpLoginStepUpAsync(request.Challenge, request.Code);
                return Ok(
                    new ApiResponse<AuthenticatedSessionResponse>(
                        "Sign-in verification successful.",
                        CreateSessionResponse(response)
                    )
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] VerifyTotpStepUp failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("recovery/password")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<VerificationChallengeResponse>), StatusCodes.Status200OK)]
        public Task<IActionResult> RecoverPassword([FromBody] PasswordRecoveryRequest request) =>
            RecoverPasswordInternalAsync(request);

        [HttpPost("forgot-password")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(ApiResponse<VerificationChallengeResponse>), StatusCodes.Status200OK)]
        public Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request) =>
            RecoverPasswordInternalAsync(request);

        private async Task<IActionResult> RecoverPasswordInternalAsync(
            PasswordRecoveryRequest request
        )
        {
            try
            {
                if (!_seedBypass.IsBypassEnabledForUsername(request.Username)
                    && !await _captchaService.VerifyCaptchaAsync(request.Captcha))
                    throw new BadRequestException("Invalid captcha.");

                var challenge = await _authService.RecoverPasswordAsync(request.Username);

                return StatusCode(
                    200,
                    new ApiResponse<VerificationChallengeResponse>(
                        "If the account exists, recovery instructions have been sent.",
                        new VerificationChallengeResponse
                        {
                            Challenge = challenge.Challenge,
                            ExpiresAtUtc = challenge.ExpiresAtUtc,
                        }
                    )
                );
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] RecoverPassword failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("recovery/username")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> RecoverUsername([FromBody] UsernameRecoveryRequest request)
        {
            try
            {
                if (!_seedBypass.IsBypassEnabledFor(request.Email)
                    && !await _captchaService.VerifyCaptchaAsync(request.Captcha))
                    throw new BadRequestException("Invalid captcha.");

                await _authService.RecoverUsernameAsync(request.Email);
                return Ok(new MessageResponse(
                    "If the account exists, recovery instructions have been sent."
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] RecoverUsername failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("reset-password")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public Task<IActionResult> ResetPassword(
            [FromBody] ResetPasswordRequest request,
            [FromQuery] string? token
        ) => ResetPasswordInternalAsync(request, token);

        [HttpPost("change-password")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting(RateLimiterConfiguration.AuthPolicyName)]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public Task<IActionResult> ChangePassword(
            [FromBody] ChangePasswordRequest request,
            [FromQuery] string? token
        ) => ResetPasswordInternalAsync(request, token);

        private async Task<IActionResult> ResetPasswordInternalAsync(
            ResetPasswordRequest request,
            string? token
        )
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    await _authService.ResetPasswordAsync(token, request.Password);
                }
                else if (!string.IsNullOrWhiteSpace(request.Code)
                    && !string.IsNullOrWhiteSpace(request.Challenge))
                {
                    await _authService.ResetPasswordWithOtpAsync(
                        request.Code,
                        request.Challenge,
                        request.Password
                    );
                }
                else
                {
                    throw new BadRequestException("Missing password reset token or OTP challenge.");
                }

                return Ok(new MessageResponse("Password reset successful. Please sign in."));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[AuthController] ResetPassword failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        private AuthenticatedSessionResponse CreateSessionResponse(
            User user,
            Token token,
            string? returnPath = null
        )
        {
            string? refreshToken = null;
            string? sessionBindingToken = null;

            if (token.Transport.UsesBrowserCookies())
            {
                HttpUtility.SetBrowserRefreshSession(
                    Response,
                    token.RefreshToken,
                    token.SessionBindingToken,
                    token.RefreshTokenLifetime
                );
            }
            else
            {
                refreshToken = token.RefreshToken;
                sessionBindingToken = token.SessionBindingToken;
            }

            return new AuthenticatedSessionResponse(
                token.AccessToken,
                token.AccessTokenExpiresAtUtc,
                refreshToken,
                sessionBindingToken,
                returnPath
            );
        }

        private AuthenticatedSessionResponse CreateSessionResponse(AuthenticatedSessionResult result)
        {
            return CreateSessionResponse(
                result.UserToken.user,
                result.UserToken.token,
                result.ReturnPath
            );
        }

        private LoginAuthenticationResponse CreateLoginAuthenticationResponse(LoginAuthenticationResult result)
        {
            return new LoginAuthenticationResponse
            {
                Type = result.Type,
                Auth = result.Session != null ? CreateSessionResponse(result.Session) : null,
                StepUp = result.StepUp
            };
        }

        private OAuthAuthenticationResponse CreateOAuthAuthenticationResponse(OAuthAuthenticationResult result)
        {
            return new OAuthAuthenticationResponse
            {
                Type = result.Type,
                Auth = result.Session != null ? CreateSessionResponse(result.Session) : null,
                StepUp = result.StepUp,
                RoleSelection = result.PendingSignup == null
                    ? null
                    : new OAuthRoleSelectionResponse
                    {
                        SignupToken = result.PendingSignup.SignupToken,
                        Email = result.PendingSignup.Email,
                        Name = result.PendingSignup.Name,
                        Provider = result.PendingSignup.Provider,
                    }
            };
        }

        private static string ResolveLoginMessage(string type) => type switch
        {
            AuthFlowResponseTypes.RequiresStepUp => "Additional sign-in verification is required.",
            _ => "Login successful"
        };

        private static string ResolveOAuthMessage(string type) => type switch
        {
            AuthFlowResponseTypes.RequiresRoleSelection => "Role selection is required to complete signup.",
            AuthFlowResponseTypes.RequiresStepUp => "Additional sign-in verification is required.",
            _ => "Login successful"
        };

        private static CurrentUserResponse CreateCurrentUserResponse(User user)
        {
            return new CurrentUserResponse
            {
                Id = user.Id,
                Email = user.Email,
                Username = string.IsNullOrWhiteSpace(user.Username) ? user.Email : user.Username,
                Name = user.Name,
                Avatar = user.Avatar,
                Usertype = AuthRoles.NormalizeStored(user.Usertype),
            };
        }

        private string? BuildFrontendAuthUrl(string path, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return null;

            var frontendBaseUrl = (
                _configuration["Frontend:BaseUrl"]
                ?? _configuration["FRONTEND_URL"]
                ?? DefaultFrontendUrl
            ).TrimEnd('/');

            return $"{frontendBaseUrl}/auth/{path}?token={Uri.EscapeDataString(token)}";
        }
    }
}

