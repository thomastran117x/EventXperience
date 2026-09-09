using backend.main.application.features;
using backend.main.application.security;
using backend.main.features.clubs.discussions.replies.contracts.requests;
using backend.main.features.clubs.discussions.replies.contracts.responses;
using backend.main.features.clubs.realtime;
using backend.main.shared.responses;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.main.features.clubs.discussions.replies;

/// <summary>
/// Nested replies and reactions for club discussions. Live delivery of these changes is
/// handled by <see cref="ClubRealtimeHub"/>.
/// </summary>
[ApiController]
[FeatureGate(FeatureFlagKeys.ClubsDiscussions)]
[Route("clubs")]
public sealed class ClubDiscussionReplyController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly IClubDiscussionReplyService _replyService;
    private readonly IClubRealtimeNotifier _notifier;

    public ClubDiscussionReplyController(
        IClubDiscussionReplyService replyService,
        IClubRealtimeNotifier notifier)
    {
        _replyService = replyService;
        _notifier = notifier;
    }

    [AllowAnonymous]
    [HttpGet("{clubId}/discussions/{discussionId}/replies")]
    [ProducesResponseType(typeof(ApiResponse<DiscussionReplyPageResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReplies(
        int clubId,
        int discussionId,
        [FromQuery] int? parentReplyId = null,
        [FromQuery] DiscussionReplySort sort = DiscussionReplySort.Newest,
        [FromQuery] string? cursor = null,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        var (userId, userRole) = GetOptionalUser();
        pageSize = Math.Clamp(pageSize < 1 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var page = await _replyService.GetPageAsync(
            clubId, discussionId, parentReplyId, sort, cursor, pageSize, userId, userRole);
        var response = new DiscussionReplyPageResponse
        {
            Items = page.Items.Select(item => MapReply(item)),
            TotalCount = page.TotalCount,
            NextCursor = page.NextCursor,
            HasMore = page.HasMore
        };
        return Ok(new ApiResponse<DiscussionReplyPageResponse>("Discussion replies fetched successfully.", response));
    }

    [Authorize]
    [HttpPost("{clubId}/discussions/{discussionId}/replies")]
    [ProducesResponseType(typeof(ApiResponse<DiscussionReplyResponse>), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateReply(
        int clubId, int discussionId, [FromBody] DiscussionReplyCreateRequest request)
    {
        var user = User.GetUserPayload();
        var view = await _replyService.CreateAsync(
            clubId, discussionId, request.ParentReplyId, user.Id, user.Role, request.Content);
        var response = MapReply(view);
        await _notifier.ReplyCreatedAsync(clubId, response);
        return StatusCode(201, new ApiResponse<DiscussionReplyResponse>("Reply created successfully.", response));
    }

    [Authorize]
    [HttpPut("{clubId}/discussions/{discussionId}/replies/{replyId}")]
    [ProducesResponseType(typeof(ApiResponse<DiscussionReplyResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateReply(
        int clubId, int discussionId, int replyId, [FromBody] DiscussionReplyUpdateRequest request)
    {
        var user = User.GetUserPayload();
        var view = await _replyService.UpdateAsync(
            clubId, discussionId, replyId, user.Id, user.Role, request.Content);
        var response = MapReply(view);
        // The broadcast deliberately strips the editor's own reaction so other viewers do
        // not inherit it; the caller's own 200 body keeps it.
        await _notifier.ReplyUpdatedAsync(clubId, MapReply(view, false));
        return Ok(new ApiResponse<DiscussionReplyResponse>("Reply updated successfully.", response));
    }

    [Authorize]
    [HttpDelete("{clubId}/discussions/{discussionId}/replies/{replyId}")]
    [ProducesResponseType(typeof(ApiResponse<DiscussionReplyResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteReply(int clubId, int discussionId, int replyId)
    {
        var user = User.GetUserPayload();
        var view = await _replyService.DeleteAsync(clubId, discussionId, replyId, user.Id, user.Role);
        var response = MapReply(view);
        await _notifier.ReplyDeletedAsync(clubId, response);
        return Ok(new ApiResponse<DiscussionReplyResponse>("Reply deleted successfully.", response));
    }

    [Authorize]
    [HttpPut("{clubId}/discussions/{discussionId}/replies/{replyId}/reaction")]
    [ProducesResponseType(typeof(ApiResponse<DiscussionReplyReactionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetReaction(
        int clubId, int discussionId, int replyId, [FromBody] DiscussionReplyReactionRequest request)
    {
        var user = User.GetUserPayload();
        var summary = await _replyService.SetReactionAsync(
            clubId, discussionId, replyId, user.Id, user.Role, request.Reaction!.Value);
        return await PublishReactionAsync(clubId, discussionId, replyId, summary);
    }

    [Authorize]
    [HttpDelete("{clubId}/discussions/{discussionId}/replies/{replyId}/reaction")]
    [ProducesResponseType(typeof(ApiResponse<DiscussionReplyReactionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearReaction(int clubId, int discussionId, int replyId)
    {
        var user = User.GetUserPayload();
        var summary = await _replyService.ClearReactionAsync(
            clubId, discussionId, replyId, user.Id, user.Role);
        return await PublishReactionAsync(clubId, discussionId, replyId, summary);
    }

    private async Task<IActionResult> PublishReactionAsync(
        int clubId, int discussionId, int replyId, DiscussionReplyReactionSummary summary)
    {
        var response = new DiscussionReplyReactionResponse
        {
            ReplyId = replyId,
            LikeCount = summary.LikeCount,
            DislikeCount = summary.DislikeCount,
            CurrentUserReaction = summary.CurrentUserReaction?.ToString()
        };
        await _notifier.ReplyReactionChangedAsync(
            clubId, discussionId, replyId, summary.LikeCount, summary.DislikeCount);
        return Ok(new ApiResponse<DiscussionReplyReactionResponse>("Reply reaction updated successfully.", response));
    }

    private (int? UserId, string? UserRole) GetOptionalUser()
    {
        if (User.Identity?.IsAuthenticated != true)
            return (null, null);
        var user = User.GetUserPayload();
        return (user.Id, user.Role);
    }

    private static DiscussionReplyResponse MapReply(
        DiscussionReplyView view, bool includeCurrentUserReaction = true)
    {
        var reply = view.Reply;
        return new DiscussionReplyResponse
        {
            Id = reply.Id,
            DiscussionId = reply.DiscussionId,
            ParentReplyId = reply.ParentReplyId,
            UserId = reply.IsDeleted ? null : reply.UserId,
            Content = reply.IsDeleted ? null : reply.Content,
            Author = reply.IsDeleted ? null : new AuthorInfo
            {
                Id = reply.UserId,
                Name = view.Author?.Name,
                Username = view.Author?.Username,
                UsernameDisplay = view.Author?.UsernameDisplay ?? view.Author?.Username,
                Avatar = view.Author?.Avatar
            },
            IsDeleted = reply.IsDeleted,
            CreatedAt = reply.CreatedAt,
            UpdatedAt = reply.UpdatedAt,
            LikeCount = view.LikeCount,
            DislikeCount = view.DislikeCount,
            CurrentUserReaction = includeCurrentUserReaction
                ? view.CurrentUserReaction?.ToString()
                : null,
            DirectReplyCount = view.DirectReplyCount
        };
    }
}
