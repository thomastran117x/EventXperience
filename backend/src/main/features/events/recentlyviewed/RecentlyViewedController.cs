using System.ComponentModel.DataAnnotations;

using backend.main.application.features;
using backend.main.application.security;
using backend.main.features.events.contracts.requests;
using backend.main.features.events.recentlyviewed.contracts.requests;
using backend.main.features.events.recentlyviewed.contracts.responses;
using backend.main.shared.exceptions.http;
using backend.main.shared.responses;
using backend.main.shared.utilities.logger;
using backend.main.utilities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.main.features.events.recentlyviewed
{
    /// <summary>
    /// Recently-viewed endpoints: the history a user builds simply by browsing, as opposed to the
    /// deliberate saves the favourites slice records.
    /// </summary>
    [ApiController]
    [FeatureGate(FeatureFlagKeys.EventsRecentlyViewed)]
    [Route("events")]
    public class RecentlyViewedController : ControllerBase
    {
        private readonly IRecentlyViewedService _recentlyViewedService;

        public RecentlyViewedController(IRecentlyViewedService recentlyViewedService)
        {
            _recentlyViewedService = recentlyViewedService;
        }

        [Authorize]
        [HttpPost("{eventId}/view")]
        [ProducesResponseType(typeof(ApiResponse<RecordEventViewResponse>), StatusCodes.Status201Created)]
        public async Task<IActionResult> RecordView([Range(1, int.MaxValue)] int eventId)
        {
            try
            {
                var user = User.GetUserPayload();

                var recorded = await _recentlyViewedService.RecordViewAsync(eventId, user.Id, user.Role);

                return StatusCode(201, new ApiResponse<RecordEventViewResponse>(
                    recorded.Recorded
                        ? $"Event with ID {eventId} has been added to your recently viewed events."
                        : "View tracking is turned off, so nothing was recorded.",
                    recorded
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[RecentlyViewedController] RecordView failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        // Literal segments declared alongside the "{eventId}/..." route; ASP.NET prefers literals,
        // as the existing events/me/pinned and events/me/waitlisted routes rely on.
        [Authorize]
        [HttpGet("me/recently-viewed")]
        [ProducesResponseType(typeof(ApiResponse<IEnumerable<RecentlyViewedEventResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyRecentlyViewed()
        {
            try
            {
                var user = User.GetUserPayload();

                var recent = await _recentlyViewedService.GetMyRecentlyViewedAsync(user.Id, user.Role);

                return Ok(new ApiResponse<IEnumerable<RecentlyViewedEventResponse>>(
                    "Your recently viewed events have been fetched successfully.",
                    recent
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[RecentlyViewedController] GetMyRecentlyViewed failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [Authorize]
        [HttpDelete("me/recently-viewed")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> ClearMyRecentlyViewed()
        {
            try
            {
                var user = User.GetUserPayload();

                var removed = await _recentlyViewedService.ClearAsync(user.Id);

                return Ok(new MessageResponse(
                    "Your recently viewed events have been cleared.",
                    new
                    {
                        removed
                    }
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[RecentlyViewedController] ClearMyRecentlyViewed failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [Authorize]
        [HttpDelete("me/recently-viewed/batch")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> RemoveManyFromMyRecentlyViewed([FromBody] BatchDeleteRequest request)
        {
            try
            {
                var user = User.GetUserPayload();

                var removed = await _recentlyViewedService.RemoveManyAsync(request.Ids, user.Id);

                return Ok(new MessageResponse(
                    $"{removed} event(s) have been removed from your recently viewed events.",
                    new
                    {
                        removed
                    }
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[RecentlyViewedController] RemoveManyFromMyRecentlyViewed failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [Authorize]
        [HttpDelete("me/recently-viewed/{eventId}")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> RemoveFromMyRecentlyViewed([Range(1, int.MaxValue)] int eventId)
        {
            try
            {
                var user = User.GetUserPayload();

                // Idempotent, so a stale entry the expiry sweep already collected is still a
                // success - the row the caller wanted gone is gone either way.
                await _recentlyViewedService.RemoveAsync(eventId, user.Id);

                return Ok(new MessageResponse(
                    $"Event with ID {eventId} has been removed from your recently viewed events."
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[RecentlyViewedController] RemoveFromMyRecentlyViewed failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [Authorize]
        [HttpPost("me/recently-viewed/merge")]
        [ProducesResponseType(typeof(ApiResponse<RecentlyViewedMergeResultResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> MergeMyRecentlyViewed([FromBody] MergeRecentlyViewedRequest request)
        {
            try
            {
                var user = User.GetUserPayload();

                var result = await _recentlyViewedService.MergeAsync(request, user.Id, user.Role);

                return Ok(new ApiResponse<RecentlyViewedMergeResultResponse>(
                    "Your recently viewed events have been synced.",
                    result
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[RecentlyViewedController] MergeMyRecentlyViewed failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [Authorize]
        [HttpGet("me/recently-viewed/settings")]
        [ProducesResponseType(typeof(ApiResponse<RecentlyViewedSettingsResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyRecentlyViewedSettings()
        {
            try
            {
                var user = User.GetUserPayload();

                var settings = await _recentlyViewedService.GetSettingsAsync(user.Id);

                return Ok(new ApiResponse<RecentlyViewedSettingsResponse>(
                    "Your recently viewed settings have been fetched successfully.",
                    settings
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[RecentlyViewedController] GetMyRecentlyViewedSettings failed: {e}");
                return HandleError.Resolve(e);
            }
        }

        [Authorize]
        [HttpPut("me/recently-viewed/settings")]
        [ProducesResponseType(typeof(ApiResponse<RecentlyViewedSettingsResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> UpdateMyRecentlyViewedSettings([FromBody] UpdateRecentlyViewedSettingsRequest request)
        {
            try
            {
                var user = User.GetUserPayload();

                var settings = await _recentlyViewedService.UpdateSettingsAsync(request.Enabled!.Value, user.Id);

                return Ok(new ApiResponse<RecentlyViewedSettingsResponse>(
                    settings.Enabled
                        ? "View tracking has been turned on."
                        : "View tracking has been turned off. Your existing history has been kept.",
                    settings
                ));
            }
            catch (Exception e)
            {
                if (e is AppException)
                    return HandleError.Resolve(e);

                Logger.Error($"[RecentlyViewedController] UpdateMyRecentlyViewedSettings failed: {e}");
                return HandleError.Resolve(e);
            }
        }
    }
}
