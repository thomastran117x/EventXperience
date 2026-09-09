using backend.main.application.features;
using backend.main.application.security;
using backend.main.features.clubs.posts;
using backend.main.features.clubs.posts.contracts.requests;
using backend.main.features.clubs.posts.contracts.responses;
using backend.main.features.clubs.posts.search;
using backend.main.features.profile.contracts;
using backend.main.shared.responses;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.main.features.clubs.posts
{
    /// <summary>
    /// Club post feeds, editing, and administrative post-management endpoints.
    /// </summary>
    [ApiController]
    [FeatureGate(FeatureFlagKeys.ClubsPosts)]
    [Route("clubs")]
    public class ClubPostController : ControllerBase
    {
        private readonly IClubPostService _postService;

        public ClubPostController(IClubPostService postService)
        {
            _postService = postService;
        }

        [Authorize]
        [HttpPost("{clubId}/posts")]
        [ProducesResponseType(typeof(ApiResponse<ClubPostResponse>), StatusCodes.Status201Created)]
        public async Task<IActionResult> CreatePost(int clubId, [FromBody] ClubPostCreateRequest request)
        {
            var userPayload = User.GetUserPayload();

            ClubPost post = await _postService.CreateAsync(
                clubId, userPayload.Id, userPayload.Role, request.Title, request.Content, request.PostType, request.IsPinned);

            return StatusCode(
                201,
                new ApiResponse<ClubPostResponse>(
                    $"Post for club with ID {clubId} has been created successfully.",
                    MapToResponse(post)
                )
            );
        }

        [AllowAnonymous]
        [HttpGet("{clubId}/posts/{id}")]
        [ProducesResponseType(typeof(ApiResponse<ClubPostResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetPost(int clubId, int id)
        {
            int? userId = null;
            string? userRole = null;
            if (User.Identity?.IsAuthenticated == true)
            {
                var p = User.GetUserPayload();
                userId = p.Id;
                userRole = p.Role;
            }

            var (post, author) = await _postService.GetByIdAsync(clubId, id, userId, userRole);

            return StatusCode(200, new ApiResponse<ClubPostResponse>(
                $"Post with ID {id} has been fetched successfully.",
                MapToResponse(post, author)
            ));
        }

        [AllowAnonymous]
        [HttpGet("{clubId}/posts")]
        [ProducesResponseType(typeof(ApiResponse<PagedResponse<ClubPostResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetPosts(
            int clubId,
            [FromQuery] string? search,
            [FromQuery] PostSortBy sortBy = PostSortBy.Recent,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            int? userId = null;
            string? userRole = null;
            if (User.Identity?.IsAuthenticated == true)
            {
                var userPayload = User.GetUserPayload();
                userId = userPayload.Id;
                userRole = userPayload.Role;
            }

            var (items, totalCount, source, authors) = await _postService.GetByClubIdAsync(
                clubId, userId, userRole, search, sortBy, page, pageSize);

            var paged = new PagedResponse<ClubPostResponse>(
                items.Select(p => MapToResponse(p, authors.GetValueOrDefault(p.UserId))),
                totalCount,
                page,
                pageSize
            );

            return StatusCode(
                200,
                new ApiResponse<PagedResponse<ClubPostResponse>>(
                    $"Posts for club with ID {clubId} have been fetched successfully.",
                    paged,
                    source
                )
            );
        }

        [Authorize]
        [HttpPut("{clubId}/posts/{id}")]
        [ProducesResponseType(typeof(ApiResponse<ClubPostResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> UpdatePost(
            int clubId,
            int id,
            [FromBody] ClubPostUpdateRequest request)
        {
            var userPayload = User.GetUserPayload();

            ClubPost post = await _postService.UpdateAsync(
                clubId, id, userPayload.Id, userPayload.Role, request.Title, request.Content, request.PostType, request.IsPinned);

            return StatusCode(
                200,
                new ApiResponse<ClubPostResponse>(
                    $"Post with ID {id} has been updated successfully.",
                    MapToResponse(post)
                )
            );
        }

        [Authorize]
        [HttpDelete("{clubId}/posts/{id}")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> DeletePost(int clubId, int id)
        {
            var userPayload = User.GetUserPayload();

            await _postService.DeleteAsync(clubId, id, userPayload.Id, userPayload.Role);

            return StatusCode(
                200,
                new MessageResponse(
                    $"Post with ID {id} has been deleted successfully."
                )
            );
        }

        private static ClubPostResponse MapToResponse(ClubPost p, UserListRecord? user = null) =>
            new(p.Id, p.ClubId, p.UserId, p.Title, p.Content, p.PostType, p.LikesCount, p.ViewCount, p.IsPinned, p.CreatedAt, p.UpdatedAt)
            {
                CommentCount = p.CommentCount,
                Author = new AuthorInfo
                {
                    Id = p.UserId,
                    Name = user?.Name,
                    Username = user?.Username,
                    UsernameDisplay = user?.UsernameDisplay ?? user?.Username,
                    Avatar = user?.Avatar
                }
            };
    }

    [ApiController]
    [FeatureGate(FeatureFlagKeys.ClubsPosts)]
    [FeatureGate(FeatureFlagKeys.SearchReindex)]
    [Route("admin/clubs")]
    [Authorize("AdminOnly")]
    public class AdminClubPostController : ControllerBase
    {
        private readonly IClubPostService _postService;
        private readonly IClubPostReindexService _reindexService;

        public AdminClubPostController(IClubPostService postService, IClubPostReindexService reindexService)
        {
            _postService = postService;
            _reindexService = reindexService;
        }

        [HttpPost("posts/reindex")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public async Task<IActionResult> ReindexPosts(CancellationToken cancellationToken)
        {
            var count = await _reindexService.ReindexAllAsync(cancellationToken);
            return Ok(new ApiResponse<object>(
                "Posts reindexed successfully.",
                new
                {
                    indexed = count
                }
            ));
        }

        [HttpGet("posts")]
        [ProducesResponseType(typeof(ApiResponse<PagedResponse<ClubPostResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAllPosts(
            [FromQuery] string? search,
            [FromQuery] PostSortBy sortBy = PostSortBy.Recent,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var (items, totalCount, source) = await _postService.GetAllAdminAsync(search, sortBy, page, pageSize);

            var paged = new PagedResponse<ClubPostResponse>(
                items.Select(p => MapToResponse(p)),
                totalCount,
                page,
                pageSize
            );

            return StatusCode(
                200,
                new ApiResponse<PagedResponse<ClubPostResponse>>(
                    "All posts have been fetched successfully.",
                    paged,
                    source
                )
            );
        }

        private static ClubPostResponse MapToResponse(ClubPost p, UserListRecord? user = null) =>
            new(p.Id, p.ClubId, p.UserId, p.Title, p.Content, p.PostType, p.LikesCount, p.ViewCount, p.IsPinned, p.CreatedAt, p.UpdatedAt)
            {
                CommentCount = p.CommentCount,
                Author = new AuthorInfo
                {
                    Id = p.UserId,
                    Name = user?.Name,
                    Username = user?.Username,
                    UsernameDisplay = user?.UsernameDisplay ?? user?.Username,
                    Avatar = user?.Avatar
                }
            };
    }
}







