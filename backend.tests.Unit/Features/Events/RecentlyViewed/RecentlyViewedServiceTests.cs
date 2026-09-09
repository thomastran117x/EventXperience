using System.Text.Json;

using backend.main.features.cache;
using backend.main.features.clubs;
using backend.main.features.events;
using backend.main.features.events.recentlyviewed;
using backend.main.features.events.recentlyviewed.contracts.requests;
using backend.main.features.events.recentlyviewed.contracts.responses;
using backend.main.features.profile;
using backend.main.infrastructure.database.core;
using backend.main.shared.exceptions.http;

using FluentAssertions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Moq;

namespace backend.tests.Unit.Features.Events.RecentlyViewed;

public class RecentlyViewedServiceTests
{
    [Fact]
    public async Task RecordViewAsync_ShouldStoreRow_AndReportRecorded()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        var response = await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");

        response.EventId.Should().Be(1);
        response.Recorded.Should().BeTrue();
        response.ViewedAtUtc.Should().Be(harness.Time.GetUtcNow().UtcDateTime);

        var stored = await harness.Db.RecentlyViewedEvents.AsNoTracking().SingleAsync();
        stored.EventId.Should().Be(1);
        stored.UserId.Should().Be(harness.UserId);
    }

    [Fact]
    public async Task RecordViewAsync_ShouldBumpTimestampWithoutAddingRow_WhenViewedAgain()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        var first = await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");

        harness.Time.Now = harness.Time.Now.AddHours(3);
        var second = await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");

        second.ViewedAtUtc.Should().BeAfter(first.ViewedAtUtc!.Value);
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(1);

        var stored = await harness.Db.RecentlyViewedEvents.AsNoTracking().SingleAsync();
        stored.ViewedAt.Should().Be(second.ViewedAtUtc!.Value);
    }

    [Fact]
    public async Task RecordViewAsync_ShouldThrow_WhenUserCannotViewEvent()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();
        harness.DenyVisibility();

        var act = async () => await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");

        // Recording something the user cannot see would leak its existence back on the history page.
        await act.Should().ThrowAsync<ResourceNotFoundException>();
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RecordViewAsync_ShouldWriteNothing_WhenTrackingIsOff()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();
        await harness.Service.UpdateSettingsAsync(false, harness.UserId);

        var response = await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");

        response.Recorded.Should().BeFalse();
        response.ViewedAtUtc.Should().BeNull();
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RecordViewAsync_ShouldEvictOldest_WhenOverTheCap()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        // One more than the cap; the very first view should be the one that goes.
        for (var eventId = 1; eventId <= 51; eventId++)
        {
            harness.Time.Now = harness.Time.Now.AddMinutes(1);
            await harness.Service.RecordViewAsync(eventId, harness.UserId, "Participant");
        }

        var stored = await harness.Db.RecentlyViewedEvents.AsNoTracking()
            .Where(v => v.UserId == harness.UserId)
            .Select(v => v.EventId)
            .ToListAsync();

        stored.Should().HaveCount(50);
        stored.Should().NotContain(1);
        stored.Should().Contain(51);
    }

    [Fact]
    public async Task RecordViewAsync_ShouldNotEvict_WhenRevisitingAtTheCap()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        for (var eventId = 1; eventId <= 50; eventId++)
        {
            harness.Time.Now = harness.Time.Now.AddMinutes(1);
            await harness.Service.RecordViewAsync(eventId, harness.UserId, "Participant");
        }

        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");

        // A repeat view cannot grow the set, so nothing should have been trimmed to make room.
        var stored = await harness.Db.RecentlyViewedEvents.AsNoTracking()
            .Where(v => v.UserId == harness.UserId)
            .Select(v => v.EventId)
            .ToListAsync();

        stored.Should().HaveCount(50);
        stored.Should().Contain(1);
        stored.Should().Contain(50);
    }

    [Fact]
    public async Task GetMyRecentlyViewedAsync_ShouldReturnMostRecentFirst()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");
        harness.Time.Now = harness.Time.Now.AddMinutes(5);
        await harness.Service.RecordViewAsync(2, harness.UserId, "Participant");
        harness.Time.Now = harness.Time.Now.AddMinutes(5);
        await harness.Service.RecordViewAsync(3, harness.UserId, "Participant");

        var recent = await harness.Service.GetMyRecentlyViewedAsync(harness.UserId, "Participant");

        recent.Select(r => r.EventId).Should().ContainInOrder(3, 2, 1);
        recent.First().Event.Id.Should().Be(3);
    }

    [Fact]
    public async Task GetMyRecentlyViewedAsync_ShouldDropEventsTheUserCanNoLongerSee()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");
        harness.Time.Now = harness.Time.Now.AddMinutes(5);
        await harness.Service.RecordViewAsync(2, harness.UserId, "Participant");

        harness.HideEvents(1);

        var recent = await harness.Service.GetMyRecentlyViewedAsync(harness.UserId, "Participant");

        // Dropped rather than redacted: a redacted row would still disclose that an event the
        // user can no longer see exists.
        recent.Select(r => r.EventId).Should().Equal(2);

        // The row survives, so the entry returns if the event becomes visible again.
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task GetMyRecentlyViewedAsync_ShouldExcludeEntriesPastRetention()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");
        harness.Time.Now = harness.Time.Now.AddDays(91);
        await harness.Service.RecordViewAsync(2, harness.UserId, "Participant");

        var recent = await harness.Service.GetMyRecentlyViewedAsync(harness.UserId, "Participant");

        // The sweep runs periodically, so the read filter is what honours the 90-day promise in
        // the window before the expired row is actually collected.
        recent.Select(r => r.EventId).Should().Equal(2);
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task GetMyRecentlyViewedAsync_ShouldReturnEmpty_WhenTrackingIsOff()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");
        await harness.Service.UpdateSettingsAsync(false, harness.UserId);

        var recent = await harness.Service.GetMyRecentlyViewedAsync(harness.UserId, "Participant");

        recent.Should().BeEmpty();
        // Hidden, not deleted - switching tracking back on restores the history.
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task GetMyRecentlyViewedAsync_ShouldReturnEmpty_WhenNothingViewed()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        var recent = await harness.Service.GetMyRecentlyViewedAsync(harness.UserId, "Participant");

        recent.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveAsync_ShouldDeleteOneEntry_AndBeIdempotent()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");
        await harness.Service.RecordViewAsync(2, harness.UserId, "Participant");

        (await harness.Service.RemoveAsync(1, harness.UserId)).Should().BeTrue();
        // A second removal of the same entry is the state the caller asked for, not an error.
        (await harness.Service.RemoveAsync(1, harness.UserId)).Should().BeFalse();

        var remaining = await harness.Db.RecentlyViewedEvents.AsNoTracking()
            .Select(v => v.EventId)
            .ToListAsync();
        remaining.Should().Equal(2);
    }

    [Fact]
    public async Task RemoveAsync_ShouldSucceed_WhenTheUserCanNoLongerSeeTheEvent()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");
        harness.HideEvents(1);

        // Owning the row is authority enough; demanding current visibility would strand entries
        // forever once a private event invitation is revoked.
        (await harness.Service.RemoveAsync(1, harness.UserId)).Should().BeTrue();
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RemoveManyAsync_ShouldDeleteOnlyTheListedEntries()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        for (var eventId = 1; eventId <= 5; eventId++)
            await harness.Service.RecordViewAsync(eventId, harness.UserId, "Participant");

        var removed = await harness.Service.RemoveManyAsync([2, 4], harness.UserId);

        removed.Should().Be(2);
        var remaining = await harness.Db.RecentlyViewedEvents.AsNoTracking()
            .Select(v => v.EventId)
            .OrderBy(id => id)
            .ToListAsync();
        remaining.Should().Equal(1, 3, 5);
    }

    [Fact]
    public async Task RemoveManyAsync_ShouldIgnoreEntriesThatAreAlreadyGone()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");

        // The multi-select UI can submit an id the expiry sweep already collected.
        var removed = await harness.Service.RemoveManyAsync([1, 2, 3], harness.UserId);

        removed.Should().Be(1);
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RemoveManyAsync_ShouldReturnZero_WhenGivenNoIds()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");

        (await harness.Service.RemoveManyAsync([], harness.UserId)).Should().Be(0);
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RemoveManyAsync_ShouldNotTouchAnotherUsersHistory()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");
        await harness.Service.RecordViewAsync(1, harness.OtherUserId, "Participant");

        var removed = await harness.Service.RemoveManyAsync([1], harness.UserId);

        removed.Should().Be(1);
        var survivor = await harness.Db.RecentlyViewedEvents.AsNoTracking().SingleAsync();
        survivor.UserId.Should().Be(harness.OtherUserId);
    }

    [Fact]
    public async Task ClearAsync_ShouldWipeOnlyTheCallersHistory()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");
        await harness.Service.RecordViewAsync(2, harness.UserId, "Participant");
        await harness.Service.RecordViewAsync(1, harness.OtherUserId, "Participant");

        var removed = await harness.Service.ClearAsync(harness.UserId);

        removed.Should().Be(2);
        var survivors = await harness.Db.RecentlyViewedEvents.AsNoTracking().ToListAsync();
        survivors.Should().ContainSingle().Which.UserId.Should().Be(harness.OtherUserId);
    }

    [Fact]
    public async Task MergeAsync_ShouldInsertVisibleEntries()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();
        var viewedAt = harness.Time.GetUtcNow().UtcDateTime.AddHours(-2);

        var result = await harness.Service.MergeAsync(
            Merge((1, viewedAt), (2, viewedAt.AddMinutes(10))),
            harness.UserId,
            "Participant");

        result.Total.Should().Be(2);
        result.Merged.Should().Be(2);
        result.Skipped.Should().Be(0);
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task MergeAsync_ShouldKeepTheLaterTimestamp_WhenBothSidesKnowTheEvent()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        var serverView = await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");
        var older = serverView.ViewedAtUtc!.Value.AddHours(-5);

        await harness.Service.MergeAsync(Merge((1, older)), harness.UserId, "Participant");

        // A client timestamp must never be able to drag an entry backwards down the list.
        var stored = await harness.Db.RecentlyViewedEvents.AsNoTracking().SingleAsync();
        stored.ViewedAt.Should().Be(serverView.ViewedAtUtc!.Value);
    }

    [Fact]
    public async Task MergeAsync_ShouldAdvanceTimestamp_WhenTheClientViewIsNewer()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        var serverView = await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");
        harness.Time.Now = harness.Time.Now.AddHours(6);
        var newer = harness.Time.GetUtcNow().UtcDateTime.AddHours(-1);

        await harness.Service.MergeAsync(Merge((1, newer)), harness.UserId, "Participant");

        var stored = await harness.Db.RecentlyViewedEvents.AsNoTracking().SingleAsync();
        stored.ViewedAt.Should().Be(newer);
        stored.ViewedAt.Should().BeAfter(serverView.ViewedAtUtc!.Value);
    }

    [Fact]
    public async Task MergeAsync_ShouldClampFutureTimestamps()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();
        var now = harness.Time.GetUtcNow().UtcDateTime;

        // A skewed client clock would otherwise pin the entry to the head of the list forever.
        await harness.Service.MergeAsync(Merge((1, now.AddDays(30))), harness.UserId, "Participant");

        var stored = await harness.Db.RecentlyViewedEvents.AsNoTracking().SingleAsync();
        stored.ViewedAt.Should().Be(now);
    }

    [Fact]
    public async Task MergeAsync_ShouldSkipExpiredItems()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();
        var stale = harness.Time.GetUtcNow().UtcDateTime.AddDays(-120);

        var result = await harness.Service.MergeAsync(Merge((1, stale)), harness.UserId, "Participant");

        result.Merged.Should().Be(0);
        result.Skipped.Should().Be(1);
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MergeAsync_ShouldSilentlySkipEventsTheUserCannotSee()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();
        harness.HideEvents(2);
        var viewedAt = harness.Time.GetUtcNow().UtcDateTime.AddHours(-1);

        var result = await harness.Service.MergeAsync(
            Merge((1, viewedAt), (2, viewedAt)),
            harness.UserId,
            "Participant");

        // Counted, never reported per id: answering item by item would turn this into a probe
        // for which private events exist.
        result.Merged.Should().Be(1);
        result.Skipped.Should().Be(1);
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task MergeAsync_ShouldWriteNothing_WhenTrackingIsOff()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();
        await harness.Service.UpdateSettingsAsync(false, harness.UserId);
        var viewedAt = harness.Time.GetUtcNow().UtcDateTime.AddHours(-1);

        var result = await harness.Service.MergeAsync(Merge((1, viewedAt)), harness.UserId, "Participant");

        result.Merged.Should().Be(0);
        result.Skipped.Should().Be(1);
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MergeAsync_ShouldReturnEmptyResult_WhenNothingIsSent()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        var result = await harness.Service.MergeAsync(new MergeRecentlyViewedRequest(), harness.UserId, "Participant");

        result.Total.Should().Be(0);
        result.Merged.Should().Be(0);
        result.Skipped.Should().Be(0);
    }

    [Fact]
    public async Task MergeAsync_ShouldTrimOnce_WhenTheBatchOverflowsTheCap()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        for (var eventId = 1; eventId <= 45; eventId++)
        {
            harness.Time.Now = harness.Time.Now.AddMinutes(1);
            await harness.Service.RecordViewAsync(eventId, harness.UserId, "Participant");
        }

        var now = harness.Time.GetUtcNow().UtcDateTime;
        var incoming = Enumerable.Range(46, 20)
            .Select(id => (id, now.AddMinutes(id)))
            .ToArray();

        await harness.Service.MergeAsync(Merge(incoming), harness.UserId, "Participant");

        (await harness.Db.RecentlyViewedEvents.CountAsync(v => v.UserId == harness.UserId)).Should().Be(50);
    }

    [Fact]
    public async Task GetSettingsAsync_ShouldDefaultToEnabled_WhenNeverChanged()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        var settings = await harness.Service.GetSettingsAsync(harness.UserId);

        settings.Enabled.Should().BeTrue();
        settings.UpdatedAtUtc.Should().BeNull();
        // An absent row means enabled, so nothing is written for users who never touch it.
        (await harness.Db.RecentlyViewedSettings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpdateSettingsAsync_ShouldPersistAndInvalidateTheCache()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        var settings = await harness.Service.UpdateSettingsAsync(false, harness.UserId);

        settings.Enabled.Should().BeFalse();
        settings.UpdatedAtUtc.Should().Be(harness.Time.GetUtcNow().UtcDateTime);
        harness.RefreshCacheMock.Verify(
            cache => cache.RemoveAsync(RecentlyViewedCacheKeys.Settings(harness.UserId)),
            Times.Once);
    }

    [Fact]
    public async Task UpdateSettingsAsync_ShouldUpdateTheExistingRow_WhenToggledTwice()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        await harness.Service.UpdateSettingsAsync(false, harness.UserId);
        harness.Time.Now = harness.Time.Now.AddHours(1);
        var settings = await harness.Service.UpdateSettingsAsync(true, harness.UserId);

        settings.Enabled.Should().BeTrue();
        (await harness.Db.RecentlyViewedSettings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpdateSettingsAsync_ShouldRestoreTheHistory_WhenTrackingIsSwitchedBackOn()
    {
        await using var harness = await RecentlyViewedHarness.CreateAsync();

        await harness.Service.RecordViewAsync(1, harness.UserId, "Participant");
        await harness.Service.UpdateSettingsAsync(false, harness.UserId);
        (await harness.Service.GetMyRecentlyViewedAsync(harness.UserId, "Participant")).Should().BeEmpty();

        await harness.Service.UpdateSettingsAsync(true, harness.UserId);

        var recent = await harness.Service.GetMyRecentlyViewedAsync(harness.UserId, "Participant");
        recent.Select(r => r.EventId).Should().Equal(1);
    }

    private static MergeRecentlyViewedRequest Merge(params (int EventId, DateTime ViewedAt)[] items) => new()
    {
        Items = items
            .Select(i => new MergeRecentlyViewedItem { EventId = i.EventId, ViewedAtUtc = i.ViewedAt })
            .ToList()
    };
}

internal sealed class RecentlyViewedHarness : IAsyncDisposable
{
    private const int SeededEventCount = 70;

    private readonly SqliteConnection _connection;
    private readonly HashSet<int> _hiddenEventIds = [];
    private bool _visibilityDenied;

    public AppDatabaseContext Db { get; }
    public RecentlyViewedService Service { get; }
    public Mock<IRefreshAheadCache> RefreshCacheMock { get; }
    public FakeTimeProvider Time { get; }

    public int UserId => 2;
    public int OtherUserId => 3;

    private RecentlyViewedHarness(
        SqliteConnection connection,
        AppDatabaseContext db,
        RecentlyViewedService service,
        Mock<IRefreshAheadCache> refreshCacheMock,
        FakeTimeProvider time)
    {
        _connection = connection;
        Db = db;
        Service = service;
        RefreshCacheMock = refreshCacheMock;
        Time = time;
    }

    public static async Task<RecentlyViewedHarness> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDatabaseContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AppDatabaseContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.AddRange(
            new User { Id = 1, Email = "organizer@test.local", Name = "Organizer", Usertype = "Organizer" },
            new User { Id = 2, Email = "two@test.local", Name = "Two", Usertype = "Participant" },
            new User { Id = 3, Email = "three@test.local", Name = "Three", Usertype = "Participant" });

        db.Clubs.Add(new Club
        {
            Id = 1,
            UserId = 1,
            Name = "Recently Viewed Club",
            Description = "Recently viewed coverage club",
            Clubtype = ClubType.Gaming,
            ClubImage = "https://cdn.test/clubs/recentlyviewed.png"
        });

        var start = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var id = 1; id <= SeededEventCount; id++)
            db.Events.Add(NewEvent(id, $"Event {id}", start.AddDays(id)));

        await db.SaveChangesAsync();

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 9, 9, 0, 0, TimeSpan.Zero));
        var harnessRef = new RecentlyViewedHarness[1];

        var eventsServiceMock = new Mock<IEventsService>();
        eventsServiceMock
            .Setup(service => service.EnsureCanViewEventAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns((int eventId, int _, string _) =>
                harnessRef[0]!.CanView(eventId)
                    ? Task.CompletedTask
                    : Task.FromException(new ResourceNotFoundException($"Event {eventId} not found")));

        // Mirrors the real implementation's contract: unknown and invisible ids fall out, and the
        // requested order is preserved so the caller's ViewedAt ordering survives.
        eventsServiceMock
            .Setup(service => service.GetVisibleEventsByIds(
                It.IsAny<IEnumerable<int>>(), It.IsAny<int?>(), It.IsAny<string?>()))
            .ReturnsAsync((IEnumerable<int> ids, int? _, string? _) =>
            {
                var requested = ids.Distinct().ToList();
                var found = db.Events.AsNoTracking()
                    .Where(e => requested.Contains(e.Id))
                    .ToDictionary(e => e.Id);

                return requested
                    .Where(id => found.ContainsKey(id) && harnessRef[0]!.CanView(id))
                    .Select(id => found[id])
                    .ToList();
            });

        var refreshCacheMock = new Mock<IRefreshAheadCache>();
        refreshCacheMock.Setup(cache => cache.RemoveAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        // Always defers to the factory. Caching the settings row is a latency optimisation, and
        // the behaviour under test is what the factory computes, not what Redis remembers.
        refreshCacheMock
            .Setup(cache => cache.GetOrSetAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<RecentlyViewedSettingsResponse?>>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<double>(),
                It.IsAny<JsonSerializerOptions?>()))
            .Returns((
                string _,
                Func<Task<RecentlyViewedSettingsResponse?>> factory,
                TimeSpan _,
                TimeSpan? _,
                double _,
                JsonSerializerOptions? _) => factory());

        var service = new RecentlyViewedService(
            db,
            new RecentlyViewedRepository(db),
            eventsServiceMock.Object,
            refreshCacheMock.Object,
            Options.Create(new RecentlyViewedOptions()),
            time);

        var harness = new RecentlyViewedHarness(connection, db, service, refreshCacheMock, time);
        harnessRef[0] = harness;

        return harness;
    }

    /// <summary>Makes every event invisible, as a revoked private-event invitation would.</summary>
    public void DenyVisibility() => _visibilityDenied = true;

    /// <summary>Makes specific events invisible, leaving the rest of the history intact.</summary>
    public void HideEvents(params int[] eventIds)
    {
        foreach (var eventId in eventIds)
            _hiddenEventIds.Add(eventId);
    }

    private bool CanView(int eventId) => !_visibilityDenied && !_hiddenEventIds.Contains(eventId);

    private static backend.main.features.events.Events NewEvent(int id, string name, DateTime startTime) => new()
    {
        Id = id,
        ClubId = 1,
        Name = name,
        Description = "An event used for recently viewed service tests.",
        Location = "Student Center",
        LifecycleState = EventLifecycleState.Published,
        StartTime = startTime,
        EndTime = startTime.AddHours(2),
        maxParticipants = 10,
        registerCost = 0,
        Category = EventCategory.Other
    };

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
