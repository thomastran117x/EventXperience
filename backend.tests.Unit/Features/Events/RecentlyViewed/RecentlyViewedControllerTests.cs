using System.Security.Claims;

using backend.main.features.events.contracts.requests;
using backend.main.features.events.recentlyviewed;
using backend.main.features.events.recentlyviewed.contracts.requests;
using backend.main.features.events.recentlyviewed.contracts.responses;
using backend.main.shared.exceptions.http;
using backend.main.shared.responses;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using Moq;

namespace backend.tests.Unit.Features.Events.RecentlyViewed;

public class RecentlyViewedControllerTests
{
    private static readonly DateTime ViewedAt = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RecordView_ShouldReturnCreatedEnvelope()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.RecordViewAsync(9, 7, "Organizer"))
            .ReturnsAsync(new RecordEventViewResponse { EventId = 9, Recorded = true, ViewedAtUtc = ViewedAt });

        var controller = CreateController(service.Object);

        var result = await controller.RecordView(9);

        var created = result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(201);
        var response = created.Value.Should().BeOfType<ApiResponse<RecordEventViewResponse>>().Subject;
        response.Message.Should().Contain("has been added to your recently viewed events");
        response.Data!.Recorded.Should().BeTrue();
        service.Verify(s => s.RecordViewAsync(9, 7, "Organizer"), Times.Once);
    }

    [Fact]
    public async Task RecordView_ShouldStillSucceed_WhenTrackingIsOff()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.RecordViewAsync(9, 7, "Organizer"))
            .ReturnsAsync(new RecordEventViewResponse { EventId = 9, Recorded = false });

        var controller = CreateController(service.Object);

        var result = await controller.RecordView(9);

        // Honouring a preference is not an error, so the fire-and-forget caller has nothing to
        // branch on.
        var created = result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(201);
        var response = created.Value.Should().BeOfType<ApiResponse<RecordEventViewResponse>>().Subject;
        response.Message.Should().Contain("View tracking is turned off");
        response.Data!.Recorded.Should().BeFalse();
    }

    [Fact]
    public async Task RecordView_ShouldResolveAppException()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.RecordViewAsync(9, 7, "Organizer"))
            .ThrowsAsync(new ResourceNotFoundException("Event 9 not found"));

        var controller = CreateController(service.Object);

        var result = await controller.RecordView(9);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task RecordView_ShouldResolveUnexpectedException()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.RecordViewAsync(9, 7, "Organizer"))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var controller = CreateController(service.Object);

        var result = await controller.RecordView(9);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task GetMyRecentlyViewed_ShouldReturnTheHistory()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.GetMyRecentlyViewedAsync(7, "Organizer"))
            .ReturnsAsync([new RecentlyViewedEventResponse { EventId = 9, ViewedAtUtc = ViewedAt, Event = new() { Id = 9 } }]);

        var controller = CreateController(service.Object);

        var result = await controller.GetMyRecentlyViewed();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<ApiResponse<IEnumerable<RecentlyViewedEventResponse>>>().Subject;
        response.Data!.Should().ContainSingle().Which.EventId.Should().Be(9);
    }

    [Fact]
    public async Task GetMyRecentlyViewed_ShouldResolveUnexpectedException()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.GetMyRecentlyViewedAsync(7, "Organizer"))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var controller = CreateController(service.Object);

        var result = await controller.GetMyRecentlyViewed();

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task ClearMyRecentlyViewed_ShouldReportHowManyWent()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.ClearAsync(7)).ReturnsAsync(4);

        var controller = CreateController(service.Object);

        var result = await controller.ClearMyRecentlyViewed();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<MessageResponse>()
            .Which.Message.Should().Contain("have been cleared");
        service.Verify(s => s.ClearAsync(7), Times.Once);
    }

    [Fact]
    public async Task ClearMyRecentlyViewed_ShouldResolveUnexpectedException()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.ClearAsync(7)).ThrowsAsync(new InvalidOperationException("boom"));

        var controller = CreateController(service.Object);

        var result = await controller.ClearMyRecentlyViewed();

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task RemoveManyFromMyRecentlyViewed_ShouldPassTheSelectionThrough()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.RemoveManyAsync(It.IsAny<IEnumerable<int>>(), 7)).ReturnsAsync(2);

        var controller = CreateController(service.Object);

        var result = await controller.RemoveManyFromMyRecentlyViewed(new BatchDeleteRequest { Ids = [3, 5] });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<MessageResponse>()
            .Which.Message.Should().Contain("2 event(s) have been removed");
        service.Verify(s => s.RemoveManyAsync(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 3, 5 })), 7), Times.Once);
    }

    [Fact]
    public async Task RemoveManyFromMyRecentlyViewed_ShouldResolveUnexpectedException()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.RemoveManyAsync(It.IsAny<IEnumerable<int>>(), 7))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var controller = CreateController(service.Object);

        var result = await controller.RemoveManyFromMyRecentlyViewed(new BatchDeleteRequest { Ids = [3] });

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task RemoveFromMyRecentlyViewed_ShouldReturnOkMessage()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.RemoveAsync(9, 7)).ReturnsAsync(true);

        var controller = CreateController(service.Object);

        var result = await controller.RemoveFromMyRecentlyViewed(9);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<MessageResponse>()
            .Which.Message.Should().Contain("Event with ID 9 has been removed");
    }

    [Fact]
    public async Task RemoveFromMyRecentlyViewed_ShouldStillSucceed_WhenNothingWasStored()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.RemoveAsync(9, 7)).ReturnsAsync(false);

        var controller = CreateController(service.Object);

        var result = await controller.RemoveFromMyRecentlyViewed(9);

        // Idempotent: an entry the expiry sweep already collected is still the outcome asked for.
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task RemoveFromMyRecentlyViewed_ShouldResolveUnexpectedException()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.RemoveAsync(9, 7)).ThrowsAsync(new InvalidOperationException("boom"));

        var controller = CreateController(service.Object);

        var result = await controller.RemoveFromMyRecentlyViewed(9);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task MergeMyRecentlyViewed_ShouldReturnTheMergeResult()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.MergeAsync(It.IsAny<MergeRecentlyViewedRequest>(), 7, "Organizer"))
            .ReturnsAsync(new RecentlyViewedMergeResultResponse { Merged = 2, Skipped = 1, Total = 3 });

        var controller = CreateController(service.Object);

        var result = await controller.MergeMyRecentlyViewed(new MergeRecentlyViewedRequest());

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<ApiResponse<RecentlyViewedMergeResultResponse>>().Subject;
        response.Data!.Merged.Should().Be(2);
        response.Data!.Skipped.Should().Be(1);
    }

    [Fact]
    public async Task MergeMyRecentlyViewed_ShouldResolveUnexpectedException()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.MergeAsync(It.IsAny<MergeRecentlyViewedRequest>(), 7, "Organizer"))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var controller = CreateController(service.Object);

        var result = await controller.MergeMyRecentlyViewed(new MergeRecentlyViewedRequest());

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task GetMyRecentlyViewedSettings_ShouldReturnThePreference()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.GetSettingsAsync(7))
            .ReturnsAsync(new RecentlyViewedSettingsResponse { Enabled = true });

        var controller = CreateController(service.Object);

        var result = await controller.GetMyRecentlyViewedSettings();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ApiResponse<RecentlyViewedSettingsResponse>>()
            .Which.Data!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task GetMyRecentlyViewedSettings_ShouldResolveUnexpectedException()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.GetSettingsAsync(7)).ThrowsAsync(new InvalidOperationException("boom"));

        var controller = CreateController(service.Object);

        var result = await controller.GetMyRecentlyViewedSettings();

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task UpdateMyRecentlyViewedSettings_ShouldSayTheHistoryWasKept_WhenSwitchedOff()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.UpdateSettingsAsync(false, 7))
            .ReturnsAsync(new RecentlyViewedSettingsResponse { Enabled = false, UpdatedAtUtc = ViewedAt });

        var controller = CreateController(service.Object);

        var result = await controller.UpdateMyRecentlyViewedSettings(new UpdateRecentlyViewedSettingsRequest { Enabled = false });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<ApiResponse<RecentlyViewedSettingsResponse>>().Subject;
        // The copy has to be explicit that switching off is not a delete.
        response.Message.Should().Contain("existing history has been kept");
        response.Data!.Enabled.Should().BeFalse();
        service.Verify(s => s.UpdateSettingsAsync(false, 7), Times.Once);
    }

    [Fact]
    public async Task UpdateMyRecentlyViewedSettings_ShouldConfirmWhenSwitchedOn()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.UpdateSettingsAsync(true, 7))
            .ReturnsAsync(new RecentlyViewedSettingsResponse { Enabled = true, UpdatedAtUtc = ViewedAt });

        var controller = CreateController(service.Object);

        var result = await controller.UpdateMyRecentlyViewedSettings(new UpdateRecentlyViewedSettingsRequest { Enabled = true });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ApiResponse<RecentlyViewedSettingsResponse>>()
            .Which.Message.Should().Contain("turned on");
    }

    [Fact]
    public async Task UpdateMyRecentlyViewedSettings_ShouldResolveUnexpectedException()
    {
        var service = new Mock<IRecentlyViewedService>();
        service.Setup(s => s.UpdateSettingsAsync(It.IsAny<bool>(), 7))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var controller = CreateController(service.Object);

        var result = await controller.UpdateMyRecentlyViewedSettings(new UpdateRecentlyViewedSettingsRequest { Enabled = true });

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
    }

    private static RecentlyViewedController CreateController(IRecentlyViewedService service)
    {
        var controller = new RecentlyViewedController(service);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "7"),
                    new Claim(ClaimTypes.Name, "organizer@example.com"),
                    new Claim(ClaimTypes.Role, "Organizer")
                ], "TestAuth"))
            }
        };

        return controller;
    }
}
