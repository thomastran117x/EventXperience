using backend.main.application.features;
using backend.main.application.security;
using backend.main.features.clubs.reviews;
using backend.main.features.clubs.reviews.contracts.requests;
using backend.main.features.clubs.reviews.contracts.responses;
using backend.main.shared.responses;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.main.features.clubs.reviews
{
    /// <summary>
    /// Club review creation, editing, and administrative review lookup endpoints.
    /// </summary>
    [ApiController]
    [FeatureGate(FeatureFlagKeys.ClubsReviews)]
    [Route("clubs")]
    public class ClubReviewController : ControllerBase
    {
        private readonly IClubReviewService _reviewService;

        public ClubReviewController(IClubReviewService reviewService)
        {
            _reviewService = reviewService;
        }

        [Authorize]
        [HttpPost("{clubId}/reviews")]
        [ProducesResponseType(typeof(ApiResponse<ClubReviewResponse>), StatusCodes.Status201Created)]
        public async Task<IActionResult> CreateReview(int clubId, [FromBody] ClubReviewCreateRequest request)
        {
            var userPayload = User.GetUserPayload();

            ClubReview review = await _reviewService.CreateReviewAsync(
                clubId: clubId,
                userId: userPayload.Id,
                title: request.Title,
                rating: request.Rating,
                comment: request.Comment
            );

            ClubReviewResponse response = MapToResponse(review);

            return StatusCode(
                201,
                new ApiResponse<ClubReviewResponse>(
                    $"Review for club with ID {clubId} has been submitted successfully.",
                    response
                )
            );
        }

        [HttpGet("{clubId}/reviews")]
        [ProducesResponseType(typeof(ApiResponse<PagedResponse<ClubReviewResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetReviewsByClub(
            int clubId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var (reviews, users, totalCount) = await _reviewService.GetClubReviewsAsync(clubId, page, pageSize);

            var items = reviews.Select(r =>
            {
                var user = users.GetValueOrDefault(r.UserId);
                return new ClubReviewResponse(
                    r.Id, r.UserId, r.ClubId, r.Title, r.Rating, r.Comment, r.CreatedAt,
                    user?.Name, user?.Username, user?.Avatar, user?.UsernameDisplay ?? user?.Username);
            });

            var paged = new PagedResponse<ClubReviewResponse>(items, totalCount, page, pageSize);

            return StatusCode(
                200,
                new ApiResponse<PagedResponse<ClubReviewResponse>>(
                    $"Reviews for club with ID {clubId} have been fetched successfully.",
                    paged
                )
            );
        }

        [Authorize]
        [HttpPut("{clubId}/reviews/{reviewId}")]
        [ProducesResponseType(typeof(ApiResponse<ClubReviewResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> UpdateReview(int clubId, int reviewId, [FromBody] ClubReviewUpdateRequest request)
        {
            var userPayload = User.GetUserPayload();

            ClubReview review = await _reviewService.UpdateReviewAsync(
                clubId: clubId,
                reviewId: reviewId,
                userId: userPayload.Id,
                title: request.Title,
                rating: request.Rating,
                comment: request.Comment
            );

            ClubReviewResponse response = MapToResponse(review);

            return StatusCode(
                200,
                new ApiResponse<ClubReviewResponse>(
                    $"Review with ID {reviewId} has been updated successfully.",
                    response
                )
            );
        }

        [Authorize]
        [HttpDelete("{clubId}/reviews/{reviewId}")]
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteReview(int clubId, int reviewId)
        {
            var userPayload = User.GetUserPayload();

            await _reviewService.DeleteReviewAsync(clubId, reviewId, userPayload.Id);

            return StatusCode(
                200,
                new MessageResponse(
                    $"Review with ID {reviewId} has been deleted successfully."
                )
            );
        }

        private static ClubReviewResponse MapToResponse(ClubReview review)
        {
            return new ClubReviewResponse(
                review.Id,
                review.UserId,
                review.ClubId,
                review.Title,
                review.Rating,
                review.Comment,
                review.CreatedAt
            );
        }
    }

    [ApiController]
    [FeatureGate(FeatureFlagKeys.ClubsReviews)]
    [Route("users")]
    public class UserReviewController : ControllerBase
    {
        private readonly IClubReviewService _reviewService;

        public UserReviewController(IClubReviewService reviewService)
        {
            _reviewService = reviewService;
        }

        [Authorize("AdminOnly")]
        [HttpGet("{userId}/reviews")]
        [ProducesResponseType(typeof(ApiResponse<IEnumerable<ClubReviewResponse>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetReviewsByUser(
            int userId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            List<ClubReview> reviews = await _reviewService.GetReviewsByUserAsync(userId, page, pageSize);

            IEnumerable<ClubReviewResponse> responses = reviews.Select(r => new ClubReviewResponse(
                r.Id,
                r.UserId,
                r.ClubId,
                r.Title,
                r.Rating,
                r.Comment,
                r.CreatedAt
            ));

            return StatusCode(
                200,
                new ApiResponse<IEnumerable<ClubReviewResponse>>(
                    $"Reviews submitted by user with ID {userId} have been fetched successfully.",
                    responses
                )
            );
        }
    }
}






