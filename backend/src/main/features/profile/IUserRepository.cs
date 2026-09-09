using backend.main.features.profile;
using backend.main.features.profile.contracts;

namespace backend.main.features.profile
{
    public interface IUserRepository
    {
        Task<User?> UpdateUserAsync(int id, User updated);
        Task<User?> UpdatePartialAsync(User user);
        Task<bool> UsernameExistsAsync(string username, int excludeUserId);
        /// <summary>
        /// Deletes the user and returns the blob URLs (avatar plus cascade-deleted club,
        /// club-version and event images) that are now orphaned and should be cleaned up.
        /// Returns an empty list when the user does not exist.
        /// </summary>
        Task<IReadOnlyList<string>> DeleteUserAsync(int id);
        /// <summary>
        /// Returns a sanitized User aggregate for non-auth workflows. Password is always null.
        /// </summary>
        Task<User?> GetUserAsync(int id);
        Task<UserProfileRecord?> GetProfileByUsernameAsync(string username);
        Task<UserProfileRecord?> GetPublicProfileByUsernameOrReservationAsync(
            string username,
            DateTime utcNow
        );
        Task<UsernameChangeRecord> ChangeUsernameAsync(
            int userId,
            string username,
            string usernameDisplay,
            DateTime utcNow,
            DateTime reservedUntilUtc
        );
        Task<UserProfileRecord?> GetProfileByEmailAsync(string email);
        Task<IReadOnlyList<UserListRecord>> GetUsersAsync(
            string? role = null,
            UserReadDetailLevel detail = UserReadDetailLevel.Slim
        );
        Task<IReadOnlyList<UserListRecord>> GetByIdsAsync(
            IEnumerable<int> ids,
            UserReadDetailLevel detail = UserReadDetailLevel.Slim
        );
    }
}
