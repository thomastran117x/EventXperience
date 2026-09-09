using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using backend.main.features.auth.contracts.responses;
using backend.main.features.auth.token;
using backend.main.features.events;
using backend.main.features.events.contracts.responses;
using backend.main.features.events.recentlyviewed.contracts.responses;
using backend.main.shared.storage;
using backend.tests.Integration.Infrastructure;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

namespace backend.tests.Integration.Features.Events;

public class RecentlyViewedEndpointsTests
{
    [Fact]
    public async Task RecordingAView_ShouldStoreOneRow_AndSurfaceInTheHistory()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var organizer = await CreateUserSessionAsync(app, "rv-store-organizer@example.com", "Organizer");
        var club = await CreateClubAsync(app, organizer.Session.AccessToken, "RV Store Club");
        var ev = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id);

        var user = await CreateUserSessionAsync(app, "rv-store-user@example.com");

        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Post, $"/api/events/{ev.Id}/view", user.Session.AccessToken));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var stored = await app.QueryDbAsync(db => db.RecentlyViewedEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(v => v.EventId == ev.Id && v.UserId == user.User!.Id));
        stored.Should().NotBeNull();

        var history = await GetHistoryAsync(app, user.Session.AccessToken);
        history.Should().ContainSingle().Which.EventId.Should().Be(ev.Id);
    }

    [Fact]
    public async Task RecordingTheSameViewTwice_ShouldKeepOneRow_AndBumpTheTimestamp()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var organizer = await CreateUserSessionAsync(app, "rv-repeat-organizer@example.com", "Organizer");
        var club = await CreateClubAsync(app, organizer.Session.AccessToken, "RV Repeat Club");
        var ev = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id);

        var user = await CreateUserSessionAsync(app, "rv-repeat-user@example.com");

        await RecordViewAsync(app, user.Session.AccessToken, ev.Id);
        var first = await app.QueryDbAsync(db => db.RecentlyViewedEvents
            .AsNoTracking().SingleAsync(v => v.UserId == user.User!.Id));

        await Task.Delay(20);
        await RecordViewAsync(app, user.Session.AccessToken, ev.Id);

        // The detail page fires this on every load, so a revisit must bump rather than duplicate.
        var rows = await app.QueryDbAsync(db => db.RecentlyViewedEvents
            .AsNoTracking().Where(v => v.UserId == user.User!.Id).ToListAsync());
        rows.Should().ContainSingle();
        rows[0].ViewedAt.Should().BeOnOrAfter(first.ViewedAt);
    }

    [Fact]
    public async Task RecordingAView_ShouldReturnNotFound_WhenTheEventIsPrivate()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var organizer = await CreateUserSessionAsync(app, "rv-private-organizer@example.com", "Organizer");
        var club = await CreateClubAsync(app, organizer.Session.AccessToken, "RV Private Club");
        var ev = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, isPrivate: true);

        var outsider = await CreateUserSessionAsync(app, "rv-private-outsider@example.com");

        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Post, $"/api/events/{ev.Id}/view", outsider.Session.AccessToken));

        // Recording an event the user cannot see would leak its existence back on the history page.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var stored = await app.QueryDbAsync(db => db.RecentlyViewedEvents
            .CountAsync(v => v.UserId == outsider.User!.Id));
        stored.Should().Be(0);
    }

    [Fact]
    public async Task TheHistory_ShouldBeMostRecentlyViewedFirst()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var organizer = await CreateUserSessionAsync(app, "rv-order-organizer@example.com", "Organizer");
        var club = await CreateClubAsync(app, organizer.Session.AccessToken, "RV Order Club");
        var first = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 6);
        var second = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 7);
        var third = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 8);

        var user = await CreateUserSessionAsync(app, "rv-order-user@example.com");

        await RecordViewAsync(app, user.Session.AccessToken, first.Id);
        await Task.Delay(20);
        await RecordViewAsync(app, user.Session.AccessToken, second.Id);
        await Task.Delay(20);
        await RecordViewAsync(app, user.Session.AccessToken, third.Id);

        var history = await GetHistoryAsync(app, user.Session.AccessToken);

        history.Select(h => h.EventId).Should().Equal(third.Id, second.Id, first.Id);
        history[0].Event.Id.Should().Be(third.Id, "the full event travels with the entry");
    }

    [Fact]
    public async Task RemovingOneEntry_ShouldDropItAndBeIdempotent()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var organizer = await CreateUserSessionAsync(app, "rv-remove-organizer@example.com", "Organizer");
        var club = await CreateClubAsync(app, organizer.Session.AccessToken, "RV Remove Club");
        var kept = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 6);
        var dropped = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 7);

        var user = await CreateUserSessionAsync(app, "rv-remove-user@example.com");
        await RecordViewAsync(app, user.Session.AccessToken, kept.Id);
        await RecordViewAsync(app, user.Session.AccessToken, dropped.Id);

        var first = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Delete, $"/api/events/me/recently-viewed/{dropped.Id}", user.Session.AccessToken));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // A repeat delete is the state the caller asked for, not an error.
        var second = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Delete, $"/api/events/me/recently-viewed/{dropped.Id}", user.Session.AccessToken));
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await GetHistoryAsync(app, user.Session.AccessToken);
        history.Select(h => h.EventId).Should().Equal(kept.Id);
    }

    [Fact]
    public async Task RemovingASelection_ShouldDropExactlyThoseEntries()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var organizer = await CreateUserSessionAsync(app, "rv-batch-organizer@example.com", "Organizer");
        var club = await CreateClubAsync(app, organizer.Session.AccessToken, "RV Batch Club");
        var one = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 6);
        var two = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 7);
        var three = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 8);

        var user = await CreateUserSessionAsync(app, "rv-batch-user@example.com");
        foreach (var ev in new[] { one, two, three })
            await RecordViewAsync(app, user.Session.AccessToken, ev.Id);

        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Delete,
            "/api/events/me/recently-viewed/batch",
            user.Session.AccessToken,
            JsonContent.Create(new { ids = new[] { one.Id, three.Id } })));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await GetHistoryAsync(app, user.Session.AccessToken);
        history.Select(h => h.EventId).Should().Equal(two.Id);
    }

    [Fact]
    public async Task RemovingASelection_ShouldNotReachAnotherUsersHistory()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var organizer = await CreateUserSessionAsync(app, "rv-scope-organizer@example.com", "Organizer");
        var club = await CreateClubAsync(app, organizer.Session.AccessToken, "RV Scope Club");
        var ev = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id);

        var user = await CreateUserSessionAsync(app, "rv-scope-user@example.com");
        var bystander = await CreateUserSessionAsync(app, "rv-scope-bystander@example.com");

        await RecordViewAsync(app, user.Session.AccessToken, ev.Id);
        await RecordViewAsync(app, bystander.Session.AccessToken, ev.Id);

        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Delete,
            "/api/events/me/recently-viewed/batch",
            user.Session.AccessToken,
            JsonContent.Create(new { ids = new[] { ev.Id } })));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The delete is scoped by the caller's id as well as the event ids.
        var survivors = await app.QueryDbAsync(db => db.RecentlyViewedEvents
            .AsNoTracking().Where(v => v.EventId == ev.Id).ToListAsync());
        survivors.Should().ContainSingle().Which.UserId.Should().Be(bystander.User!.Id);
    }

    [Fact]
    public async Task RemovingASelection_ShouldRejectAnOversizedBatch()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var user = await CreateUserSessionAsync(app, "rv-toobig-user@example.com");

        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Delete,
            "/api/events/me/recently-viewed/batch",
            user.Session.AccessToken,
            JsonContent.Create(new { ids = Enumerable.Range(1, 51).ToArray() })));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ClearingTheHistory_ShouldWipeEverythingForTheCaller()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var organizer = await CreateUserSessionAsync(app, "rv-clear-organizer@example.com", "Organizer");
        var club = await CreateClubAsync(app, organizer.Session.AccessToken, "RV Clear Club");
        var one = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 6);
        var two = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 7);

        var user = await CreateUserSessionAsync(app, "rv-clear-user@example.com");
        await RecordViewAsync(app, user.Session.AccessToken, one.Id);
        await RecordViewAsync(app, user.Session.AccessToken, two.Id);

        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Delete, "/api/events/me/recently-viewed", user.Session.AccessToken));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var remaining = await app.QueryDbAsync(db => db.RecentlyViewedEvents
            .CountAsync(v => v.UserId == user.User!.Id));
        remaining.Should().Be(0);
    }

    [Fact]
    public async Task Merging_ShouldFoldABrowserHistoryIntoTheAccount()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var organizer = await CreateUserSessionAsync(app, "rv-merge-organizer@example.com", "Organizer");
        var club = await CreateClubAsync(app, organizer.Session.AccessToken, "RV Merge Club");
        var one = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 6);
        var two = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 7);

        var user = await CreateUserSessionAsync(app, "rv-merge-user@example.com");

        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Post,
            "/api/events/me/recently-viewed/merge",
            user.Session.AccessToken,
            JsonContent.Create(new
            {
                items = new[]
                {
                    new { eventId = one.Id, viewedAtUtc = DateTime.UtcNow.AddHours(-2) },
                    new { eventId = two.Id, viewedAtUtc = DateTime.UtcNow.AddHours(-1) }
                }
            })));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = (await app.ReadApiResponseAsync<RecentlyViewedMergeResultResponse>(response)).Data!;
        result.Merged.Should().Be(2);
        result.Skipped.Should().Be(0);

        var history = await GetHistoryAsync(app, user.Session.AccessToken);
        history.Select(h => h.EventId).Should().Equal(two.Id, one.Id);
    }

    [Fact]
    public async Task Merging_ShouldSkipInvisibleEventsWithoutRevealingThem()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var organizer = await CreateUserSessionAsync(app, "rv-mergepriv-organizer@example.com", "Organizer");
        var club = await CreateClubAsync(app, organizer.Session.AccessToken, "RV Merge Private Club");
        var visible = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 6);
        var hidden = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id, startsInDays: 7, isPrivate: true);

        var user = await CreateUserSessionAsync(app, "rv-mergepriv-user@example.com");

        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Post,
            "/api/events/me/recently-viewed/merge",
            user.Session.AccessToken,
            JsonContent.Create(new
            {
                items = new[]
                {
                    new { eventId = visible.Id, viewedAtUtc = DateTime.UtcNow.AddHours(-2) },
                    new { eventId = hidden.Id, viewedAtUtc = DateTime.UtcNow.AddHours(-1) }
                }
            })));

        // A 200 with a skip count, never a per-id 404 - otherwise this endpoint becomes a probe
        // for which private events exist.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await app.ReadApiResponseAsync<RecentlyViewedMergeResultResponse>(response)).Data!;
        result.Merged.Should().Be(1);
        result.Skipped.Should().Be(1);

        var history = await GetHistoryAsync(app, user.Session.AccessToken);
        history.Select(h => h.EventId).Should().Equal(visible.Id);
    }

    [Fact]
    public async Task Settings_ShouldDefaultToEnabled()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var user = await CreateUserSessionAsync(app, "rv-settings-default@example.com");

        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Get, "/api/events/me/recently-viewed/settings", user.Session.AccessToken));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = (await app.ReadApiResponseAsync<RecentlyViewedSettingsResponse>(response)).Data!;
        settings.Enabled.Should().BeTrue();
        settings.UpdatedAtUtc.Should().BeNull();

        // No row is written for a user who never touches the toggle.
        var rows = await app.QueryDbAsync(db => db.RecentlyViewedSettings
            .CountAsync(s => s.UserId == user.User!.Id));
        rows.Should().Be(0);
    }

    [Fact]
    public async Task TurningTrackingOff_ShouldHideTheHistoryButKeepTheRows()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var organizer = await CreateUserSessionAsync(app, "rv-optout-organizer@example.com", "Organizer");
        var club = await CreateClubAsync(app, organizer.Session.AccessToken, "RV Opt Out Club");
        var ev = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id);

        var user = await CreateUserSessionAsync(app, "rv-optout-user@example.com");
        await RecordViewAsync(app, user.Session.AccessToken, ev.Id);

        await SetTrackingAsync(app, user.Session.AccessToken, false);

        (await GetHistoryAsync(app, user.Session.AccessToken)).Should().BeEmpty();

        // Switching off stops collection; it is not a delete, and the rows have to survive so the
        // history comes back if the user changes their mind.
        var stored = await app.QueryDbAsync(db => db.RecentlyViewedEvents
            .CountAsync(v => v.UserId == user.User!.Id));
        stored.Should().Be(1);

        await SetTrackingAsync(app, user.Session.AccessToken, true);
        (await GetHistoryAsync(app, user.Session.AccessToken)).Should().ContainSingle();
    }

    [Fact]
    public async Task RecordingAView_ShouldWriteNothing_WhileTrackingIsOff()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var organizer = await CreateUserSessionAsync(app, "rv-offrecord-organizer@example.com", "Organizer");
        var club = await CreateClubAsync(app, organizer.Session.AccessToken, "RV Off Record Club");
        var ev = await CreateEventAsync(app, organizer.Session.AccessToken, club.Id);

        var user = await CreateUserSessionAsync(app, "rv-offrecord-user@example.com");
        await SetTrackingAsync(app, user.Session.AccessToken, false);

        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Post, $"/api/events/{ev.Id}/view", user.Session.AccessToken));

        // Honouring the preference is a success, not an error - the client fires and forgets.
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var recorded = (await app.ReadApiResponseAsync<RecordEventViewResponse>(response)).Data!;
        recorded.Recorded.Should().BeFalse();

        var stored = await app.QueryDbAsync(db => db.RecentlyViewedEvents
            .CountAsync(v => v.UserId == user.User!.Id));
        stored.Should().Be(0);
    }

    [Fact]
    public async Task TheHistory_ShouldRequireAuthentication()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.Client.GetAsync("/api/events/me/recently-viewed");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task RecordViewAsync(AuthApiTestApp app, string accessToken, int eventId)
    {
        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Post, $"/api/events/{eventId}/view", accessToken));

        if (response.StatusCode != HttpStatusCode.Created)
            throw new Xunit.Sdk.XunitException(await app.DescribeFailureAsync(response));
    }

    private static async Task<List<RecentlyViewedEventResponse>> GetHistoryAsync(
        AuthApiTestApp app, string accessToken)
    {
        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Get, "/api/events/me/recently-viewed", accessToken));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await app.ReadApiResponseAsync<List<RecentlyViewedEventResponse>>(response)).Data!;
    }

    private static async Task SetTrackingAsync(AuthApiTestApp app, string accessToken, bool enabled)
    {
        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Put,
            "/api/events/me/recently-viewed/settings",
            accessToken,
            JsonContent.Create(new { enabled })));

        if (response.StatusCode != HttpStatusCode.OK)
            throw new Xunit.Sdk.XunitException(await app.DescribeFailureAsync(response));
    }

    private static async Task<(AuthenticatedSessionResponse Session, backend.main.features.profile.User? User)>
        CreateUserSessionAsync(AuthApiTestApp app, string email, string role = "Participant")
    {
        var session = await app.SignUpAndVerifyByTokenAsync(
            email, role: role, transport: SessionTransportResolver.ApiValue);
        var user = await app.FindUserByEmailAsync(email);
        return (session, user);
    }

    private static async Task<ClubApiModel> CreateClubAsync(AuthApiTestApp app, string accessToken, string name)
    {
        var response = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Post,
            "/api/clubs",
            accessToken,
            JsonContent.Create(new
            {
                Name = name,
                Description = "Recently viewed testing group",
                Clubtype = "social",
                ClubImageUrl = app.BlobStorage.CreateOwnedBlobUrl("clubs", "club.png"),
                Email = $"{name.Replace(" ", "-", StringComparison.OrdinalIgnoreCase).ToLowerInvariant()}@example.com"
            })));

        if (response.StatusCode != HttpStatusCode.Created)
            throw new Xunit.Sdk.XunitException(await app.DescribeFailureAsync(response));

        return (await app.ReadApiResponseAsync<ClubApiModel>(response)).Data!;
    }

    /// <summary>Creates and publishes a free event with room to spare, public unless asked otherwise.</summary>
    private static async Task<EventResponse> CreateEventAsync(
        AuthApiTestApp app,
        string accessToken,
        int clubId,
        int startsInDays = 6,
        bool isPrivate = false)
    {
        var presigned = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Post,
            "/api/events/images/presigned-url",
            accessToken,
            JsonContent.Create(new { clubId, fileName = "poster.png", contentType = "image/png" })));
        presigned.StatusCode.Should().Be(HttpStatusCode.OK);
        var image = (await app.ReadApiResponseAsync<PresignedUploadResponse>(presigned)).Data!;

        var created = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/events/clubs/{clubId}/drafts",
            accessToken,
            JsonContent.Create(new
            {
                name = $"RV Event D{startsInDays}{(isPrivate ? " Private" : string.Empty)}",
                description = "A published event used for recently viewed integration coverage.",
                location = "Student Center",
                imageUrls = new[] { image.PublicUrl },
                isPrivate,
                maxParticipants = 10,
                registerCost = 0,
                startTime = DateTime.UtcNow.AddDays(startsInDays),
                endTime = DateTime.UtcNow.AddDays(startsInDays).AddHours(2),
                category = EventCategory.Other,
                venueName = "Room A",
                city = "Toronto",
                tags = new[] { "testing" }
            })));

        if (created.StatusCode != HttpStatusCode.Created)
            throw new Xunit.Sdk.XunitException(await app.DescribeFailureAsync(created));

        var draft = (await app.ReadApiResponseAsync<ManagedEventResponse>(created)).Data!;

        var published = await app.Client.SendAsync(CreateAuthorizedRequest(
            HttpMethod.Post, $"/api/events/{draft.Id}/publish", accessToken, JsonContent.Create(new { })));

        if (published.StatusCode != HttpStatusCode.OK)
            throw new Xunit.Sdk.XunitException(await app.DescribeFailureAsync(published));

        var managed = (await app.ReadApiResponseAsync<ManagedEventResponse>(published)).Data!;
        return new EventResponse
        {
            Id = managed.Id,
            Name = managed.Name ?? string.Empty,
            MaxParticipants = managed.MaxParticipants ?? 0,
            ClubId = managed.ClubId
        };
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string path,
        string accessToken,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = content;
        return request;
    }

    private sealed class ClubApiModel
    {
        public int Id
        {
            get; set;
        }
        public string Name { get; set; } = string.Empty;
    }
}
