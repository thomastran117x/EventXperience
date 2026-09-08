using backend.main.features.auth.contracts;
using backend.main.features.profile;
using backend.main.features.profile.contracts;

namespace backend.main.features.auth
{
    public interface IAuthUserRepository
    {
        Task<User> CreateUserAsync(User user);
        Task<User?> UpdateUserAsync(int id, User updated);
        Task<UserStatusRecord?> UpdateUserStatusAsync(int id, bool isDisabled, string? disabledReason);
        Task<bool> IncrementAuthVersionAsync(int id);
        Task<User?> GetUserAsync(int id);
        Task<UserAuthRecord?> GetAuthByUsernameAsync(string username);
        Task<UserAuthRecord?> GetAuthByEmailAsync(string email);
        /// <summary>
        /// Credentials looked up by account id rather than by address, for callers whose
        /// whole purpose is that the address is about to change.
        /// </summary>
        Task<UserAuthRecord?> GetAuthByIdAsync(int id);
        Task<UserRecoveryRecord?> GetRecoveryByUsernameAsync(string username);
        Task<UserRecoveryRecord?> GetRecoveryByEmailAsync(string email);
        Task<bool> UsernameUnavailableAsync(string username, DateTime utcNow);
        Task<UserOAuthRecord?> GetOAuthByEmailAsync(string email);
        Task<UserOAuthRecord?> GetOAuthByMicrosoftIdAsync(string microsoftId);
        Task<UserOAuthRecord?> GetOAuthByGoogleIdAsync(string googleId);
        Task<UserOAuthRecord?> UpdateProviderIdsAsync(int id, string? googleId, string? microsoftId);
        Task<bool> EmailExistsAsync(string email);
        /// <summary>
        /// Swaps the account's address and bumps <c>AuthVersion</c> in one commit, so outstanding
        /// access tokens carrying the old address stop validating the moment the change lands.
        /// </summary>
        Task<EmailChangeRecord> ChangeEmailAsync(int userId, string email, DateTime utcNow);
    }
}
