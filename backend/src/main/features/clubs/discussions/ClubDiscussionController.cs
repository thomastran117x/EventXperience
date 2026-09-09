using backend.main.application.features;
using backend.main.application.security;
using backend.main.features.clubs.discussions.contracts.requests;
using backend.main.features.clubs.discussions.contracts.responses;
using backend.main.features.profile.contracts;
using backend.main.shared.responses;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.main.features.clubs.discussions
{
    /// <summary>
    /// Member-authored club discussion topics: creation, listing, and author-owned editing.
    /// </summary>
    [ApiController]
    [FeatureGate(FeatureFlagKeys.ClubsDiscussions)]
    [Route("clubs")]
    public class ClubDiscussionController : ControllerBase
    {
        private const int DefaultPageSize = 20;
        private const int MaxPageSize = 100;

        private readonly IClubDiscussionService _discussionService;

        public ClubDiscussionController(IClubDiscussionService discussionService)
        {
            _discussionService = discussionService;
        }

        [Authorize]
        [HttpPost("{clubId}/discussions")]
        [ProducesResponseType(typeof(ApiResponse<ClubDiscussionResponse>), StatusCodes.Status201Created)]
        public async Task<IActionResult> CreateDiscussion(int clubId, [FromBody] ClubDiscussionCreateRequest request)
        {
            var userPayload = User.GetUserPayload();

            ClubDiscussion discussion = await _discussionService.CreateAsync(
                clubId: clubId,
                userId: userPayload.Id,
                userRole: userPayload.Role,
                title: request.Title,
                description: request.Description
            );

            return StatusCode(
                201,
                new ApiResponse<ClubDiscussionResponse>(
                    $"Discussion for club with ID {clubId} has been created successfully.",
                    MapToResponse(discussion)
                )
            );
        }

        [AllowAnonymous]
        [HttpGet("{clubId}/discussions")]
        [ProducesResponseType(typeof(ApiResponse<PagedResponse<ClubDiscussionResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetDiscussionsByClub(
            int clubId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = DefaultPageSize)
        {
            int? userId = null;
            string? userRole = null;
            if (User.Identity?.IsAuthenticated == true)
            {
                var userPayload = User.GetUserPayload();
                userId = userPayload.Id;
                userRole = userPayload.Role;
            }

            page = page < 1 ? 1 : page;
            pageSize = pageSize < 1 ? DefaultPageSize : pageSize > MaxPageSize ? MaxPageSize : pageSize;

            var (discussions, authors, totalCount) =
                await _discussionService.GetByClubIdAsync(clubId, userId, userRole, page, pageSize);

            var items = discussions.Select(d => MapToResponse(d, authors.GetValueOrDefault(d.UserId)));
            var paged = new PagedResponse<ClubDiscussionResponse>(items, totalCount, page, pageSize);

            return StatusCode(
                200,
                new ApiResponse<PagedResponse<ClubDiscussionResponse>>(
                    $"Discussions for club with ID {clubId} have been fetched successfully.",
                    paged
                )
            );
        }

        [Authorize]
        [HttpPut("{clubId}/discussions/{discussionId}")]
        [ProducesResponseType(typeof(ApiResponse<ClubDiscussionResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> UpdateDiscussion(
            int clubId,
            int discussionId,
            [FromBody] ClubDiscussionUpdateRequest request)
        {
            var userPayload = User.GetUserPayload();

            ClubDiscussion discussion = await _discussionService.UpdateAsync(
                clubId: clubId,
                discussionId: discussionId,
                userId: userPayload.Id,
                title: request.Title,
                description: request.Description
            );

            return StatusCode(
                200,
                new ApiResponse<ClubDiscussionResponse>(
                    $"Discussion with ID {discussionId} has been updated successfully.",
                    MapToResponse(discussion)
                )
            );
        }

        [Authorize]
        [HttpDelete("{clubId}/discussions/{discussionId}")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteDiscussion(int clubId, int discussionId)
        {
            var userPayload = User.GetUserPayload();

            await _discussionService.DeleteAsync(clubId, discussionId, userPayload.Id);

            return StatusCode(
                200,
                new MessageResponse(
                    $"Discussion with ID {discussionId} has been deleted successfully."
                )
            );
        }

        private static ClubDiscussionResponse MapToResponse(ClubDiscussion discussion, UserListRecord? author = null)
        {
            return new ClubDiscussionResponse(
                discussion.Id,
                discussion.ClubId,
                discussion.UserId,
                discussion.Title,
                discussion.Description,
                discussion.CreatedAt,
                discussion.UpdatedAt
            )
            {
                ReplyCount = discussion.ReplyCount,
                Author = new AuthorInfo
                {
                    Id = discussion.UserId,
                    Name = author?.Name,
                    Username = author?.Username,
                    UsernameDisplay = author?.UsernameDisplay ?? author?.Username,
                    Avatar = author?.Avatar
                }
            };
        }
    }
}
