using backend.main.application.features;
using backend.main.application.security;
using backend.main.features.auth;
using backend.main.features.auth.contracts.responses;
using backend.main.features.auth.token;
using backend.main.features.profile.contracts.requests;
using backend.main.features.profile.contracts.responses;
using backend.main.features.profile.email;
using backend.main.shared.exceptions.http;
using backend.main.shared.responses;
using backend.main.shared.utilities.logger;
using backend.main.utilities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace backend.main.features.profile
{
    [ApiController]
    [FeatureGate(FeatureFlagKeys.Profile)]
    [Route("profile")]
    [Authorize]
    public class ProfileController : ControllerBase
    {
        private readonly IUserService _userService;
        private readonly IAuthService _authService;
        private readonly ITokenService _tokenService;
        private readonly IEmailChangeService _emailChangeService;
        private readonly TimeProvider _timeProvider;

        public ProfileController(
            IUserService userService,
            IAuthService authService,
            ITokenService tokenService,
            IEmailChangeService emailChangeService,
            TimeProvider timeProvider
        )
        {
            _userService = userService;
            _authService = authService;
            _tokenService = tokenService;
            _emailChangeService = emailChangeService;
            _timeProvider = timeProvider;
        }

        [HttpGet]
        [ProducesResponseType(typeof(ApiResponse<MyProfileResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyProfile()
        {
            try
            {
                var userPayload = User.GetUserPayload();
                var user = await _userService.GetUserByIdAsync(userPayload.Id);

                return Ok(new ApiResponse<MyProfileResponse>(
                    "Profile fetched successfully.",
                    MapToMyProfile(user)
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[ProfileController] GetMyProfile failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpGet("{username}")]
        [AllowAnonymous]
        [ProducesResponseType(typeof(ApiResponse<PublicProfileResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetPublicProfile(string username)
        {
            try
            {
                var profile = await _userService.GetPublicProfileByUsernameAsync(username);

                return Ok(new ApiResponse<PublicProfileResponse>(
                    "Profile fetched successfully.",
                    new PublicProfileResponse
                    {
                        Username = profile.Username,
                        UsernameDisplay = profile.UsernameDisplay,
                        Name = profile.Name,
                        Avatar = profile.Avatar,
                        Usertype = profile.Usertype,
                        CreatedAtUtc = profile.CreatedAtUtc,
                    }
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[ProfileController] GetPublicProfile failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPatch]
        [ProducesResponseType(typeof(ApiResponse<MyProfileResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
        {
            try
            {
                var userPayload = User.GetUserPayload();

                var updatedUser = await _userService.UpdateUserAsync(
                    userPayload.Id,
                    new User
                    {
                        // Email/Usertype are required by the User type but are intentionally
                        // ignored by UpdatePartialAsync — they are never persisted from here.
                        Id = userPayload.Id,
                        Email = userPayload.Email,
                        Usertype = userPayload.Role,
                        Name = request.Name,
                        Phone = request.Phone,
                        Address = request.Address,
                    }
                );

                if (updatedUser == null)
                    throw new ResourceNotFoundException("User not found.");

                return Ok(new ApiResponse<MyProfileResponse>(
                    "Profile updated successfully.",
                    MapToMyProfile(updatedUser)
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[ProfileController] UpdateProfile failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPatch("username")]
        [RequireMfa]
        [ProducesResponseType(typeof(ApiResponse<MyProfileResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status403Forbidden)]
        [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status409Conflict)]
        public async Task<IActionResult> ChangeUsername([FromBody] ChangeUsernameRequest request)
        {
            try
            {
                var userPayload = User.GetUserPayload();
                var updatedUser = await _userService.ChangeUsernameAsync(
                    userPayload.Id,
                    request.Username);

                return Ok(new ApiResponse<MyProfileResponse>(
                    "Username changed successfully.",
                    MapToMyProfile(updatedUser)
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[ProfileController] ChangeUsername failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("avatar")]
        // Raise the per-request cap above Kestrel's 1 MB global default to match the 5 MB
        // avatar limit enforced by AvatarUploadRequest; otherwise larger uploads are rejected
        // by the server before model validation runs.
        [RequestSizeLimit(5 * 1024 * 1024)]
        [ProducesResponseType(typeof(ApiResponse<MyProfileResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> UploadAvatar([FromForm] AvatarUploadRequest request)
        {
            try
            {
                var userPayload = User.GetUserPayload();
                var updatedUser = await _userService.UpdateAvatarAsync(userPayload.Id, request.Image);

                if (updatedUser == null)
                    throw new ResourceNotFoundException("User not found.");

                return Ok(new ApiResponse<MyProfileResponse>(
                    "Avatar updated successfully.",
                    MapToMyProfile(updatedUser)
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[ProfileController] UploadAvatar failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("change-password")]
        [RequireMfa]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordAuthenticatedRequest request)
        {
            try
            {
                var userPayload = User.GetUserPayload();
                await _authService.ChangePasswordForAuthenticatedUserAsync(
                    userPayload.Email,
                    request.CurrentPassword,
                    request.NewPassword
                );

                return Ok(new MessageResponse("Password changed successfully. Please sign in again."));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[ProfileController] ChangePassword failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpPost("email")]
        [RequireMfa]
        [EnableRateLimiting(RateLimiterConfiguration.EmailChangePolicyName)]
        [ProducesResponseType(typeof(ApiResponse<VerificationChallengeResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponse<object?>), StatusCodes.Status409Conflict)]
        public async Task<IActionResult> RequestEmailChange([FromBody] ChangeEmailRequest request)
        {
            try
            {
                var userPayload = User.GetUserPayload();
                var challenge = await _emailChangeService.RequestChangeAsync(
                    userPayload.Id,
                    request.NewEmail,
                    request.CurrentPassword,
                    HttpContext.RequestAborted
                );

                return Ok(new ApiResponse<VerificationChallengeResponse>(
                    "Check your new email address for a confirmation link and code.",
                    new VerificationChallengeResponse
                    {
                        Challenge = challenge.Challenge,
                        ExpiresAtUtc = challenge.ExpiresAtUtc,
                    }
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[ProfileController] RequestEmailChange failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpGet("email/pending")]
        [ProducesResponseType(typeof(ApiResponse<PendingEmailChangeResponse?>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetPendingEmailChange()
        {
            try
            {
                var userPayload = User.GetUserPayload();
                var pending = await _emailChangeService.GetPendingAsync(userPayload.Id);

                return Ok(new ApiResponse<PendingEmailChangeResponse?>(
                    pending == null
                        ? "No email change is pending."
                        : "Pending email change fetched successfully.",
                    pending == null
                        ? null
                        : new PendingEmailChangeResponse
                        {
                            NewEmail = pending.NewEmail,
                            ExpiresAtUtc = pending.ExpiresAtUtc,
                        }
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[ProfileController] GetPendingEmailChange failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpDelete("email/pending")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> CancelPendingEmailChange()
        {
            try
            {
                var userPayload = User.GetUserPayload();
                await _emailChangeService.CancelPendingAsync(userPayload.Id);

                return Ok(new MessageResponse("Email change cancelled."));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[ProfileController] CancelPendingEmailChange failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [HttpDelete]
        [RequireMfa]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteAccount()
        {
            try
            {
                var userPayload = User.GetUserPayload();

                await _tokenService.RevokeAllRefreshSessionsAsync(userPayload.Id);
                await _userService.DeleteUserAsync(userPayload.Id);

                HttpUtility.ClearBrowserRefreshSession(Response);

                return Ok(new MessageResponse("Account deleted successfully."));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[ProfileController] DeleteAccount failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        private MyProfileResponse MapToMyProfile(User user)
        {
            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            return new MyProfileResponse
            {
                Id = user.Id,
                Email = user.Email,
                Username = user.Username ?? string.Empty,
                UsernameDisplay = user.UsernameDisplay ?? user.Username ?? string.Empty,
                CanChangeUsername = user.UsernameChangeAvailableAtUtc == null
                    || user.UsernameChangeAvailableAtUtc <= utcNow,
                UsernameChangeAvailableAtUtc = user.UsernameChangeAvailableAtUtc,
                Name = user.Name,
                Avatar = user.Avatar,
                Usertype = user.Usertype,
                Phone = user.Phone,
                Address = user.Address,
                HasLocalPassword = user.HasLocalPassword,
                GoogleLinked = !string.IsNullOrEmpty(user.GoogleID),
                MicrosoftLinked = !string.IsNullOrEmpty(user.MicrosoftID),
                CreatedAtUtc = user.CreatedAt,
                UpdatedAtUtc = user.UpdatedAt,
            };
        }
    }
}
