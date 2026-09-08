using backend.main.features.auth;
using backend.main.features.auth.contracts;
using backend.main.features.auth.token;
using backend.main.features.cache;
using backend.main.features.clubs.follow;
using backend.main.features.profile;
using backend.main.features.profile.contracts;
using backend.main.shared.exceptions.http;
using backend.main.shared.storage;

using Microsoft.Extensions.Options;


namespace backend.main.features.profile
{
    public class UserService : IUserService
    {
        private readonly IUserRepository _userRepository;
        private readonly IAuthUserRepository _authUserRepository;
        private readonly IAzureBlobService _blobService;
        private readonly IFollowService _followService;
        private readonly ITokenService _tokenService;
        private readonly IRefreshAheadCache _refreshCache;
        private readonly IUsernameAvailabilityService _usernameAvailability;
        private readonly TimeProvider _timeProvider;
        private readonly ProfileOptions _profileOptions;

        private static readonly TimeSpan UserTTL = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan NotFoundTTL = TimeSpan.FromSeconds(15);

        private static string GetUserCacheKey(int userId) => $"user:{userId}";

        public UserService(
            IUserRepository userRepository,
            IAuthUserRepository authUserRepository,
            IAzureBlobService blobService,
            IFollowService followService,
            ITokenService tokenService,
            IRefreshAheadCache refreshCache,
            IUsernameAvailabilityService usernameAvailability,
            TimeProvider timeProvider,
            IOptions<ProfileOptions> profileOptions
        )
        {
            _userRepository = userRepository;
            _authUserRepository = authUserRepository;
            _blobService = blobService;
            _followService = followService;
            _tokenService = tokenService;
            _refreshCache = refreshCache;
            _usernameAvailability = usernameAvailability;
            _timeProvider = timeProvider;
            _profileOptions = profileOptions.Value;
        }

        public async Task<IReadOnlyList<UserListRecord>> GetAllUsersAsync(
            string? role = null,
            UserReadDetailLevel detail = UserReadDetailLevel.Slim
        )
        {
            return await _userRepository.GetUsersAsync(role, detail);
        }

        public async Task<User> GetUserByIdAsync(int id)
        {
            var user = await _refreshCache.GetOrSetAsync(
                GetUserCacheKey(id),
                () => _userRepository.GetUserAsync(id),
                UserTTL,
                nullSentinelTtl: NotFoundTTL);

            if (user == null)
                throw new ResourceNotFoundException($"User with the id {id} is not found");

            return user;
        }

        public async Task<UserProfileRecord> GetPublicProfileByUsernameAsync(string username)
        {
            // Lookup, so Normalize rather than NormalizeAndValidate: usernames created before the
            // format rules existed still have to resolve their public profile. A value that no
            // longer satisfies the rules simply misses and becomes a 404 below.
            var normalizedUsername = UsernamePolicy.Normalize(username);
            var profile = await _userRepository.GetPublicProfileByUsernameOrReservationAsync(
                normalizedUsername,
                _timeProvider.GetUtcNow().UtcDateTime);
            if (profile == null)
                throw new ResourceNotFoundException(
                    $"No user found with the username {normalizedUsername}");

            return profile;
        }

        public async Task<User?> UpdateUserAsync(int id, User updatedUser)
        {
            var existingUser = await _userRepository.UpdatePartialAsync(updatedUser);
            if (existingUser == null)
                throw new ResourceNotFoundException($"User with the id {id} is not found");

            await _refreshCache.RemoveAsync(GetUserCacheKey(id));
            return existingUser;
        }

        public async Task<User> ChangeUsernameAsync(int id, string username)
        {
            var normalizedUsername = UsernamePolicy.NormalizeAndValidate(username);
            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            var reservedUntilUtc = utcNow.AddDays(_profileOptions.UsernameChangeCooldownDays);

            var result = await _userRepository.ChangeUsernameAsync(
                id,
                normalizedUsername,
                utcNow,
                reservedUntilUtc);

            switch (result.Status)
            {
                case UsernameChangeStatus.Changed when result.User != null:
                    await _refreshCache.RemoveAsync(GetUserCacheKey(id));

                    // Both names are now occupied: the new one by the user, the old one by the
                    // reservation the change created. Recording only the new name would let the
                    // filter report the cooling-down name as free.
                    await _usernameAvailability.MarkTakenAsync(normalizedUsername);
                    if (!string.IsNullOrEmpty(result.PreviousUsername))
                        await _usernameAvailability.MarkTakenAsync(result.PreviousUsername);

                    return result.User;
                case UsernameChangeStatus.UserNotFound:
                    throw new ResourceNotFoundException($"User with the id {id} is not found");
                case UsernameChangeStatus.Unchanged:
                    throw new BadRequestException("New username must be different from the current username.");
                case UsernameChangeStatus.CooldownActive when result.AvailableAtUtc is DateTime availableAtUtc:
                    throw new UsernameChangeCooldownException(availableAtUtc);
                case UsernameChangeStatus.Unavailable:
                    throw new UsernameTakenException(normalizedUsername);
                default:
                    throw new InternalServerErrorException();
            }
        }

        public async Task<bool> DeleteUserAsync(int id)
        {
            IReadOnlyList<string> orphanedBlobs = await _userRepository.DeleteUserAsync(id);

            // Best-effort cleanup of the avatar plus any cascade-deleted club/event images
            // (no-op for external/legacy URLs). Failures are swallowed inside DeleteBlobAsync.
            foreach (string blobUrl in orphanedBlobs)
                await _blobService.DeleteBlobAsync(blobUrl);

            await _refreshCache.RemoveAsync(GetUserCacheKey(id));
            return true;
        }

        public async Task<UserStatusRecord> UpdateUserStatusAsync(int id, bool isDisabled, string? reason)
        {
            var user = await _authUserRepository.UpdateUserStatusAsync(id, isDisabled, reason);
            if (user == null)
                throw new ResourceNotFoundException($"User with the id {id} is not found");

            await _tokenService.RevokeAllRefreshSessionsAsync(id);
            await _refreshCache.RemoveAsync(GetUserCacheKey(id));
            return user;
        }

        public async Task<User?> UpdateAvatarAsync(int id, IFormFile image)
        {
            // Verify the user exists before writing anything to blob storage, so a
            // deleted/missing account can't leave an orphaned upload behind.
            User user = await _userRepository.GetUserAsync(id)
                ?? throw new ResourceNotFoundException($"User with the id {id} is not found");

            string? previousAvatar = user.Avatar;
            string filePath = await _blobService.UploadImageAsync(image, "users");
            user.Avatar = filePath;

            User updatedUser;
            try
            {
                updatedUser = await _userRepository.UpdatePartialAsync(user)
                    ?? throw new ResourceNotFoundException($"User with the id {id} is not found");
            }
            catch
            {
                // The new blob was uploaded but never persisted — best-effort delete it so the
                // failed update doesn't leave an orphan behind, then surface the original error.
                await _blobService.DeleteBlobAsync(filePath);
                throw;
            }

            // Best-effort cleanup of the replaced image (no-op for external/legacy URLs).
            if (!string.IsNullOrEmpty(previousAvatar) && previousAvatar != filePath)
                await _blobService.DeleteBlobAsync(previousAvatar);

            await _refreshCache.RemoveAsync(GetUserCacheKey(id));
            return updatedUser;
        }

        public async Task<IEnumerable<FollowClub>> GetUserFollowingsAsync(int id, int page = 1, int pageSize = 20)
        {
            return await _followService.GetFollowsByUserAsync(id, page, pageSize);
        }
    }
}
