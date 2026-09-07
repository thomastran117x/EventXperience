using backend.main.application.features;
using backend.main.application.security;
using backend.main.features.clubs.contracts.requests;
using backend.main.features.clubs.contracts.responses;
using backend.main.features.clubs.search;
using backend.main.features.clubs.versions;
using backend.main.features.clubs.versions.contracts.responses;
using backend.main.features.profile;
using backend.main.features.profile.contracts;
using backend.main.shared.exceptions.http;
using backend.main.shared.responses;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.main.features.clubs
{
    /// <summary>
    /// Club discovery, management, staff, ownership, and version-history endpoints.
    /// </summary>
    [ApiController]
    [FeatureGate(FeatureFlagKeys.Clubs)]
    [Route("clubs")]
    public class ClubController : ControllerBase
    {
        private readonly IClubService _clubService;
        private readonly IUserRepository _userRepository;

        public ClubController(IClubService clubService, IUserRepository userRepository)
        {
            _clubService = clubService;
            _userRepository = userRepository;
        }

        [Authorize]
        [FeatureGate(FeatureFlagKeys.ClubsFollow)]
        [HttpPost("{clubId}/join")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> JoinClub(int clubId)
        {
            var userPayload = User.GetUserPayload();

            await _clubService.JoinClubAsync(clubId, userPayload.Id);

            return StatusCode(
                200,
                new MessageResponse(
                    $"The club with ID `{clubId}` has been followed successfully."
                )
            );
        }

        [Authorize]
        [FeatureGate(FeatureFlagKeys.ClubsFollow)]
        [HttpDelete("{clubId}/join")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> LeaveClub(int clubId)
        {
            var userPayload = User.GetUserPayload();

            await _clubService.LeaveClubAsync(clubId, userPayload.Id);

            return StatusCode(
                200,
                new MessageResponse(
                    $"The club with ID `{clubId}` has been unfollowed successfully."
                )
            );
        }

        [Authorize]
        [HttpPost("")]
        [ProducesResponseType(typeof(ApiResponse<ClubResponse>), StatusCodes.Status201Created)]
        public async Task<IActionResult> CreateClub([FromBody] ClubCreateRequest request)
        {
            var userPayload = User.GetUserPayload();

            Club club = await _clubService.CreateClub(userPayload.Id, new ClubWriteModel
            {
                Name = request.Name,
                Description = request.Description,
                Clubtype = request.Clubtype,
                ClubImageUrl = request.ClubImageUrl,
                BannerImageUrl = request.BannerImageUrl,
                GalleryImageUrls = request.GalleryImageUrls,
                Phone = request.Phone,
                Email = request.Email,
                WebsiteUrl = request.WebsiteUrl,
                Location = request.Location,
                MaxMemberCount = request.MaxMemberCount,
                IsPrivate = request.IsPrivate
            });

            ClubResponse response = MapToResponse(club, new ClubAccessInfo
            {
                IsOwner = true,
                CanManage = true
            });

            return StatusCode(
                201,
                new ApiResponse<ClubResponse>(
                    $"The club with ID {club.Id} has been created successfully.",
                    response
                )
            );
        }

        [Authorize]
        [HttpPut("{id}")]
        [ProducesResponseType(typeof(ApiResponse<ClubResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> UpdateClub([FromBody] ClubUpdateRequest request, int id)
        {
            var userPayload = User.GetUserPayload();

            Club club = await _clubService.UpdateClub(id, userPayload.Id, userPayload.Role, new ClubWriteModel
            {
                Name = request.Name,
                Description = request.Description,
                Clubtype = request.Clubtype,
                ClubImageUrl = request.ClubImageUrl,
                BannerImageUrl = request.BannerImageUrl,
                GalleryImageUrls = request.GalleryImageUrls,
                Phone = request.Phone,
                Email = request.Email,
                WebsiteUrl = request.WebsiteUrl,
                Location = request.Location,
                MaxMemberCount = request.MaxMemberCount,
                IsPrivate = request.IsPrivate
            });

            var access = await _clubService.GetClubAccessAsync(id, userPayload.Id, userPayload.Role);
            ClubResponse response = MapToResponse(club, access);

            return StatusCode(
                200,
                new ApiResponse<ClubResponse>(
                    $"The club with ID {id} has been updated successfully.",
                    response
                )
            );
        }

        [Authorize]
        [HttpDelete("{id}")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteClub(int id)
        {
            var userPayload = User.GetUserPayload();

            await _clubService.DeleteClub(id, userPayload.Id);

            return StatusCode(
                200,
                new MessageResponse(
                    $"The club with ID {id} has been deleted successfully."
                )
            );
        }

        [HttpGet("{id}")]
        [ProducesResponseType(typeof(ApiResponse<ClubResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetClub(int id)
        {
            Club club = await _clubService.GetClub(id);
            var access = await ResolveAccessAsync([club.Id]);
            ClubResponse response = MapToResponse(club, access[club.Id]);

            return StatusCode(
                200,
                new ApiResponse<ClubResponse>(
                    $"The club with ID {id} has been fetched successfully.",
                    response
                )
            );
        }

        [HttpGet("")]
        [ProducesResponseType(typeof(ApiResponse<PagedResponse<ClubResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetClubs(
            [FromQuery] string? search,
            [FromQuery] ClubType? clubType,
            [FromQuery] ClubSortBy sortBy = ClubSortBy.Relevance,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var criteria = PublicClubSearchCriteriaFactory.FromQuery(
                search,
                clubType,
                sortBy,
                page,
                pageSize);
            var (clubs, totalCount, source) = await _clubService.GetAllClubs(criteria);

            var accessMap = await ResolveAccessAsync(clubs.Select(club => club.Id));
            IEnumerable<ClubResponse> responses = clubs.Select(club => MapToResponse(club, accessMap[club.Id]));
            var paged = new PagedResponse<ClubResponse>(responses, totalCount, criteria.Page, criteria.PageSize);

            return StatusCode(
                200,
                new ApiResponse<PagedResponse<ClubResponse>>(
                    $"The clubs have been fetched successfully.",
                    paged,
                    source
                )
            );
        }

        [HttpPost("search")]
        [ProducesResponseType(typeof(ApiResponse<PagedResponse<ClubResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> SearchClubs([FromBody] ClubSearchRequest request)
        {
            var criteria = PublicClubSearchCriteriaFactory.FromRequest(request);
            var (clubs, totalCount, source) = await _clubService.GetAllClubs(criteria);

            var accessMap = await ResolveAccessAsync(clubs.Select(club => club.Id));
            var responses = clubs.Select(club => MapToResponse(club, accessMap[club.Id]));
            var paged = new PagedResponse<ClubResponse>(responses, totalCount, criteria.Page, criteria.PageSize);

            return Ok(new ApiResponse<PagedResponse<ClubResponse>>(
                "The clubs have been fetched successfully.",
                paged,
                source
            ));
        }

        [Authorize]
        [HttpGet("managed")]
        [ProducesResponseType(typeof(ApiResponse<IEnumerable<ClubResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetManagedClubs()
        {
            var userPayload = User.GetUserPayload();
            var clubs = await _clubService.GetManagedClubsAsync(userPayload.Id);
            var accessMap = await _clubService.GetClubAccessMapAsync(
                clubs.Select(club => club.Id),
                userPayload.Id,
                userPayload.Role);

            return Ok(new ApiResponse<IEnumerable<ClubResponse>>(
                "Managed clubs have been fetched successfully.",
                clubs.Select(club => MapToResponse(club, accessMap[club.Id]))
            ));
        }

        [Authorize]
        [HttpGet("{id}/staff")]
        [ProducesResponseType(typeof(ApiResponse<IEnumerable<ClubStaffResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetClubStaff(int id, [FromQuery] string? search = null)
        {
            var userPayload = User.GetUserPayload();
            var staff = await _clubService.GetStaffAsync(id, userPayload.Id, userPayload.Role, search);
            var users = await LoadUserLookupAsync(staff.Select(member => member.UserId));

            return Ok(new ApiResponse<IEnumerable<ClubStaffResponse>>(
                $"Staff for club with ID {id} has been fetched successfully.",
                staff.Select(member => MapToStaffResponse(member, users.GetValueOrDefault(member.UserId)))
            ));
        }

        private async Task<IReadOnlyDictionary<int, UserListRecord>> LoadUserLookupAsync(IEnumerable<int> userIds)
        {
            var ids = userIds.Distinct().ToList();
            if (ids.Count == 0)
                return new Dictionary<int, UserListRecord>();

            return (await _userRepository.GetByIdsAsync(ids)).ToDictionary(user => user.Id);
        }

        private async Task<int> ResolveUserIdAsync(string identifier)
        {
            var trimmed = identifier.Trim();
            var profile = trimmed.Contains('@')
                ? await _userRepository.GetProfileByEmailAsync(EmailPolicy.Normalize(trimmed))
                : await _userRepository.GetProfileByUsernameAsync(trimmed);

            return profile?.Id
                ?? throw new ResourceNotFoundException($"No account found for '{trimmed}'.");
        }

        [Authorize]
        [HttpPost("{id}/staff/managers")]
        [ProducesResponseType(typeof(ApiResponse<ClubStaffResponse>), StatusCodes.Status201Created)]
        public async Task<IActionResult> AddManager(int id, [FromBody] ClubStaffCreateRequest request)
        {
            var userPayload = User.GetUserPayload();
            var staff = await _clubService.AddStaffAsync(
                id,
                request.UserId,
                backend.main.features.clubs.staff.ClubStaffRole.Manager,
                userPayload.Id,
                userPayload.Role);

            var users = await LoadUserLookupAsync([staff.UserId]);

            return StatusCode(201, new ApiResponse<ClubStaffResponse>(
                $"Manager has been added to club with ID {id} successfully.",
                MapToStaffResponse(staff, users.GetValueOrDefault(staff.UserId))
            ));
        }

        [Authorize]
        [HttpPost("{id}/staff/volunteers")]
        [ProducesResponseType(typeof(ApiResponse<ClubStaffResponse>), StatusCodes.Status201Created)]
        public async Task<IActionResult> AddVolunteer(int id, [FromBody] ClubStaffCreateRequest request)
        {
            var userPayload = User.GetUserPayload();
            var staff = await _clubService.AddStaffAsync(
                id,
                request.UserId,
                backend.main.features.clubs.staff.ClubStaffRole.Volunteer,
                userPayload.Id,
                userPayload.Role);

            var users = await LoadUserLookupAsync([staff.UserId]);

            return StatusCode(201, new ApiResponse<ClubStaffResponse>(
                $"Volunteer has been added to club with ID {id} successfully.",
                MapToStaffResponse(staff, users.GetValueOrDefault(staff.UserId))
            ));
        }

        [Authorize]
        [HttpDelete("{id}/staff/{userId}")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> RemoveStaff(int id, int userId)
        {
            var userPayload = User.GetUserPayload();
            await _clubService.RemoveStaffAsync(id, userId, userPayload.Id, userPayload.Role);

            return Ok(new MessageResponse(
                $"Staff member with user ID {userId} has been removed from club with ID {id} successfully."
            ));
        }

        [Authorize]
        [HttpPost("{id}/transfer-ownership")]
        [ProducesResponseType(typeof(ApiResponse<ClubResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> TransferOwnership(int id, [FromBody] ClubOwnershipTransferRequest request)
        {
            var userPayload = User.GetUserPayload();
            var newOwnerUserId = string.IsNullOrWhiteSpace(request.NewOwnerIdentifier)
                ? request.NewOwnerUserId
                : await ResolveUserIdAsync(request.NewOwnerIdentifier);

            var club = await _clubService.TransferOwnershipAsync(
                id,
                newOwnerUserId,
                userPayload.Id,
                userPayload.Role);

            var access = await _clubService.GetClubAccessAsync(id, userPayload.Id, userPayload.Role);
            return Ok(new ApiResponse<ClubResponse>(
                $"Ownership for club with ID {id} has been transferred successfully.",
                MapToResponse(club, access)
            ));
        }

        [Authorize]
        [FeatureGate(FeatureFlagKeys.ClubsVersioning)]
        [HttpGet("{id}/versions")]
        [ProducesResponseType(typeof(ApiResponse<PagedResponse<ClubVersionListItemResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetClubVersions(
            int id,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var effectivePage = page < 1 ? 1 : page;
            var effectivePageSize = pageSize switch
            {
                < 1 => 20,
                > 100 => 100,
                _ => pageSize
            };

            var userPayload = User.GetUserPayload();
            var (items, totalCount) = await _clubService.GetVersionHistoryAsync(
                id,
                userPayload.Id,
                userPayload.Role,
                effectivePage,
                effectivePageSize);

            var actors = await LoadUserLookupAsync(items.Select(item => item.ActorUserId));

            var paged = new PagedResponse<ClubVersionListItemResponse>(
                items.Select(item => MapToVersionListItemResponse(item, actors.GetValueOrDefault(item.ActorUserId))),
                totalCount,
                effectivePage,
                effectivePageSize);

            return Ok(new ApiResponse<PagedResponse<ClubVersionListItemResponse>>(
                $"Version history for club with ID {id} has been fetched successfully.",
                paged
            ));
        }

        [Authorize]
        [FeatureGate(FeatureFlagKeys.ClubsVersioning)]
        [HttpGet("{id}/versions/{versionNumber}")]
        [ProducesResponseType(typeof(ApiResponse<ClubVersionDetailResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetClubVersion(int id, int versionNumber)
        {
            var userPayload = User.GetUserPayload();
            var version = await _clubService.GetVersionDetailAsync(
                id,
                versionNumber,
                userPayload.Id,
                userPayload.Role);

            var actors = await LoadUserLookupAsync([version.ActorUserId]);

            return Ok(new ApiResponse<ClubVersionDetailResponse>(
                $"Version {versionNumber} for club with ID {id} has been fetched successfully.",
                MapToVersionDetailResponse(version, actors.GetValueOrDefault(version.ActorUserId))
            ));
        }

        [Authorize]
        [FeatureGate(FeatureFlagKeys.ClubsVersioning)]
        [HttpPost("{id}/versions/{versionNumber}/rollback")]
        [ProducesResponseType(typeof(ApiResponse<ClubRollbackResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> RollbackClubVersion(int id, int versionNumber)
        {
            var userPayload = User.GetUserPayload();
            var result = await _clubService.RollbackToVersionAsync(
                id,
                versionNumber,
                userPayload.Id,
                userPayload.Role);

            var response = new ClubRollbackResponse(
                MapToResponse(result.Club),
                result.RestoredFromVersionNumber,
                result.NewVersionNumber);

            return Ok(new ApiResponse<ClubRollbackResponse>(
                $"Club with ID {id} has been rolled back to version {versionNumber} successfully.",
                response
            ));
        }

        private async Task<Dictionary<int, ClubAccessInfo>> ResolveAccessAsync(IEnumerable<int> clubIds)
        {
            if (User.Identity?.IsAuthenticated != true)
                return clubIds.Distinct().ToDictionary(id => id, _ => new ClubAccessInfo());

            var userPayload = User.GetUserPayload();
            return await _clubService.GetClubAccessMapAsync(clubIds, userPayload.Id, userPayload.Role);
        }

        private static ClubResponse MapToResponse(Club club, ClubAccessInfo? access = null)
        {
            var response = new ClubResponse(
                club.Id,
                club.UserId,
                club.Name,
                club.Description,
                club.Clubtype.ToString(),
                club.ClubImage,
                club.MemberCount,
                club.EventCount,
                club.AvaliableEventCount,
                club.MaxMemberCount,
                club.isPrivate,
                club.CurrentVersionNumber
            )
            {
                BannerImage = club.BannerImage,
                GalleryImages = club.GalleryImages ?? [],
                Phone = club.Phone,
                Email = club.Email,
                Rating = club.Rating,
                WebsiteUrl = club.WebsiteUrl,
                Location = club.Location,
                IsOwner = access?.IsOwner ?? false,
                IsManager = access?.IsManager ?? false,
                IsVolunteer = access?.IsVolunteer ?? false,
                CanManage = access?.CanManage ?? false
            };

            return response;
        }

        private static ClubStaffResponse MapToStaffResponse(
            backend.main.features.clubs.staff.ClubStaff staff,
            UserListRecord? user = null) =>
            new(
                staff.Id,
                staff.ClubId,
                staff.UserId,
                staff.Role.ToString(),
                staff.GrantedByUserId,
                staff.CreatedAt,
                staff.UpdatedAt,
                user?.Name,
                user?.Username,
                user?.Avatar
            );

        private static ClubVersionListItemResponse MapToVersionListItemResponse(
            ClubVersionHistoryItem item,
            UserListRecord? actor = null) =>
            new(
                item.VersionNumber,
                item.ActionType,
                item.CreatedAt,
                item.ActorUserId,
                item.ActorRole,
                item.RollbackEligible,
                item.RollbackExpiresAt,
                item.RollbackSourceVersionNumber,
                item.ChangedFields.Select(MapToFieldChangeResponse).ToList(),
                actor?.Name,
                actor?.Username,
                actor?.Avatar
            );

        private static ClubVersionDetailResponse MapToVersionDetailResponse(
            ClubVersionDetail detail,
            UserListRecord? actor = null) =>
            new(
                detail.VersionNumber,
                detail.ActionType,
                detail.CreatedAt,
                detail.ActorUserId,
                detail.ActorRole,
                detail.RollbackEligible,
                detail.RollbackExpiresAt,
                detail.RollbackSourceVersionNumber,
                detail.ChangedFields.Select(MapToFieldChangeResponse).ToList(),
                new ClubVersionSnapshotResponse(
                    detail.Snapshot.Name,
                    detail.Snapshot.Description,
                    detail.Snapshot.Clubtype,
                    detail.Snapshot.ClubImage,
                    detail.Snapshot.Phone,
                    detail.Snapshot.Email,
                    detail.Snapshot.WebsiteUrl,
                    detail.Snapshot.Location,
                    detail.Snapshot.MaxMemberCount,
                    detail.Snapshot.IsPrivate
                ),
                actor?.Name,
                actor?.Username,
                actor?.Avatar
            );

        private static ClubVersionFieldChangeResponse MapToFieldChangeResponse(ClubVersionFieldChange change) =>
            new(change.Field, change.OldValue, change.NewValue);
    }

    [ApiController]
    [FeatureGate(FeatureFlagKeys.Clubs)]
    [FeatureGate(FeatureFlagKeys.SearchReindex)]
    [Route("admin/clubs")]
    [Authorize("AdminOnly")]
    public class AdminClubsController : ControllerBase
    {
        private readonly IClubReindexService _reindexService;

        public AdminClubsController(IClubReindexService reindexService)
        {
            _reindexService = reindexService;
        }

        [HttpPost("reindex")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public async Task<IActionResult> ReindexClubs(CancellationToken cancellationToken)
        {
            var count = await _reindexService.ReindexAllAsync(cancellationToken);
            return Ok(new ApiResponse<object>(
                "Clubs reindexed successfully.",
                new
                {
                    indexed = count
                }
            ));
        }
    }
}










