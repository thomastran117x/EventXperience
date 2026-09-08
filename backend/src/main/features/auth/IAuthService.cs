using backend.main.features.auth.contracts.responses;
using backend.main.features.auth.oauth;
using backend.main.features.auth.token;
using backend.main.features.profile;

namespace backend.main.features.auth
{
    public interface IAuthService
    {
        Task<LoginAuthenticationResult> LoginAsync(
            string username,
            string password,
            SessionTransport transport,
            bool rememberMe = false,
            string? returnUrl = null
        );
        Task<VerificationOtpChallenge> SignUpAsync(string email, string username, string password, string usertype);
        Task<UserToken> VerifyAsync(string token, SessionTransport transport);
        Task<UserToken> VerifyOtpAsync(string code, string challenge, SessionTransport transport);
        Task<AuthenticatedSessionResult> VerifyDeviceLoginAsync(string token, SessionTransport transport);
        Task<StartLoginStepUpResponse> StartLoginStepUpAsync(string challenge, string method);
        Task<AuthenticatedSessionResult> VerifyLoginStepUpAsync(string challenge, string code);
        Task<AuthenticatedSessionResult> VerifyTotpLoginStepUpAsync(string challenge, string code);
        Task<OAuthAuthenticationResult> GoogleAsync(
            string token,
            SessionTransport transport,
            string? expectedNonce = null,
            string? returnUrl = null
        );
        Task<OAuthAuthenticationResult> GoogleCodeAsync(
            string code,
            string codeVerifier,
            string redirectUri,
            SessionTransport transport,
            string? nonce = null,
            string? returnUrl = null
        );
        Task<OAuthAuthenticationResult> MicrosoftAsync(
            string token,
            SessionTransport transport,
            string? expectedNonce = null,
            string? returnUrl = null
        );
        Task<UserToken> CompleteOAuthSignupAsync(
            string signupToken,
            string usertype,
            string? username,
            SessionTransport transport
        );
        Task<User> GetCurrentUserAsync(int userId);
        Task<UserToken> HandleTokensAsync(
            string refreshToken,
            string? sessionBindingToken,
            SessionTransport transport
        );
        Task HandleLogoutAsync(
            string refreshToken,
            string? sessionBindingToken,
            SessionTransport transport
        );
        Task<VerificationOtpChallenge> RecoverPasswordAsync(string username);
        Task RecoverUsernameAsync(string email);
        Task ResetPasswordAsync(string token, string password);
        Task ResetPasswordWithOtpAsync(string code, string challenge, string password);
        Task ChangePasswordForAuthenticatedUserAsync(string email, string currentPassword, string newPassword);
    }
}
