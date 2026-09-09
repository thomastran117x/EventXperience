using backend.main.application.features;
using backend.main.application.security;
using backend.main.features.clubs.follow;
using backend.main.features.clubs.follow.contracts.responses;
using backend.main.shared.responses;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.main.features.clubs.follow
{
    /// <summary>
    /// Membership and follow-list endpoints for clubs and users.
    /// </summary>
    [ApiController]
    [FeatureGate(FeatureFlagKeys.ClubsFollow)]
    [Route("clubs")]
    public class ClubFollowController : ControllerBase
    {
        private readonly IFollowService _followService;

        public ClubFollowController(IFollowService followService)
        {
            _followService = followService;
        }

        [HttpGet("{clubId}/members")]
        [ProducesResponseType(typeof(ApiResponse<PagedResponse<ClubMemberResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetClubMembers(
            int clubId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? search = null)
        {
            var (members, users, totalCount) = await _followService.GetClubMembersAsync(clubId, page, pageSize, search);

            var items = members.Select(m =>
            {
                var user = users.GetValueOrDefault(m.UserId);
                return new ClubMemberResponse(
                    m.Id, m.UserId, m.ClubId, m.CreatedAt,
                    user?.Name, user?.Username, user?.UsernameDisplay ?? user?.Username, user?.Avatar);
            });

            var paged = new PagedResponse<ClubMemberResponse>(items, totalCount, page, pageSize);

            return StatusCode(
                200,
                new ApiResponse<PagedResponse<ClubMemberResponse>>(
                    $"Members for club with ID {clubId} have been fetched successfully.",
                    paged
                )
            );
        }

        [Authorize]
        [HttpGet("{clubId}/members/me")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public async Task<IActionResult> CheckMembership(int clubId)
        {
            var userPayload = User.GetUserPayload();

            bool isMember = await _followService.IsMemberAsync(clubId, userPayload.Id);

            return StatusCode(
                200,
                new ApiResponse<object>(
                    $"Membership status for club with ID {clubId} has been fetched successfully.",
                    new
                    {
                        isMember
                    }
                )
            );
        }
    }

    [ApiController]
    [FeatureGate(FeatureFlagKeys.ClubsFollow)]
    [Route("users")]
    public class UserFollowController : ControllerBase
    {
        private readonly IFollowService _followService;

        public UserFollowController(IFollowService followService)
        {
            _followService = followService;
        }

        [Authorize]
        [HttpGet("{userId}/clubs/following")]
        [ProducesResponseType(typeof(ApiResponse<IEnumerable<FollowResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetClubsFollowedByUser(
            int userId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            IEnumerable<FollowClub> follows = await _followService.GetFollowsByUserAsync(userId, page, pageSize);

            IEnumerable<FollowResponse> responses = follows.Select(f =>
                new FollowResponse(f.Id, f.UserId, f.ClubId, f.CreatedAt)
            );

            return StatusCode(
                200,
                new ApiResponse<IEnumerable<FollowResponse>>(
                    $"Clubs followed by user with ID {userId} have been fetched successfully.",
                    responses
                )
            );
        }
    }
}







