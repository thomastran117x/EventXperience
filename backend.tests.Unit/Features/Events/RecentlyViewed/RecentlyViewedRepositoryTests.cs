using backend.main.features.clubs;
using backend.main.features.events;
using backend.main.features.events.recentlyviewed;
using backend.main.features.profile;
using backend.main.infrastructure.database.core;

using FluentAssertions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace backend.tests.Unit.Features.Events.RecentlyViewed;

public class RecentlyViewedRepositoryTests
{
    private static readonly DateTime Now = new(2026, 9, 9, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetRecentAsync_ShouldOrderMostRecentFirst()
    {
        await using var harness = await RepositoryHarness.CreateAsync();

        await harness.SeedAsync(
            (1, 1, Now.AddHours(-3)),
            (1, 2, Now.AddHours(-1)),
            (1, 3, Now.AddHours(-2)));

        var rows = await harness.Repository.GetRecentAsync(1, Now.AddDays(-90), 50);

        rows.Select(r => r.EventId).Should().Equal(2, 3, 1);
    }

    [Fact]
    public async Task GetRecentAsync_ShouldExcludeEntriesOlderThanTheCutoff()
    {
        await using var harness = await RepositoryHarness.CreateAsync();

        await harness.SeedAsync(
            (1, 1, Now.AddDays(-91)),
            (1, 2, Now.AddDays(-1)));

        var rows = await harness.Repository.GetRecentAsync(1, Now.AddDays(-90), 50);

        rows.Select(r => r.EventId).Should().Equal(2);
    }

    [Fact]
    public async Task GetRecentAsync_ShouldCapAtTheLimit()
    {
        await using var harness = await RepositoryHarness.CreateAsync();

        var entries = Enumerable.Range(1, 20)
            .Select(id => (1, id, Now.AddMinutes(-id)))
            .ToArray();
        await harness.SeedAsync(entries);

        var rows = await harness.Repository.GetRecentAsync(1, Now.AddDays(-90), 5);

        // The read cap is the invariant guard: concurrent trims and the bulk merge can both race
        // the write path, and the presented list has to stay correct regardless.
        rows.Should().HaveCount(5);
        rows.Select(r => r.EventId).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task GetRecentAsync_ShouldReturnEmpty_WhenLimitIsNotPositive()
    {
        await using var harness = await RepositoryHarness.CreateAsync();

        await harness.SeedAsync((1, 1, Now));

        (await harness.Repository.GetRecentAsync(1, Now.AddDays(-90), 0)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecentAsync_ShouldOnlyReturnTheGivenUsersRows()
    {
        await using var harness = await RepositoryHarness.CreateAsync();

        await harness.SeedAsync(
            (1, 1, Now.AddHours(-1)),
            (2, 2, Now.AddHours(-1)));

        var rows = await harness.Repository.GetRecentAsync(1, Now.AddDays(-90), 50);

        rows.Should().ContainSingle().Which.EventId.Should().Be(1);
    }

    [Fact]
    public async Task GetSettingAsync_ShouldReturnNull_WhenTheUserNeverChangedIt()
    {
        await using var harness = await RepositoryHarness.CreateAsync();

        // An absent row is the enabled default, not a missing record.
        (await harness.Repository.GetSettingAsync(1)).Should().BeNull();
    }

    [Fact]
    public async Task GetSettingAsync_ShouldReturnTheStoredPreference()
    {
        await using var harness = await RepositoryHarness.CreateAsync();

        harness.Db.RecentlyViewedSettings.Add(new RecentlyViewedSetting { UserId = 1, Enabled = false, UpdatedAt = Now });
        await harness.Db.SaveChangesAsync();

        var setting = await harness.Repository.GetSettingAsync(1);

        setting.Should().NotBeNull();
        setting!.Enabled.Should().BeFalse();
    }

    private sealed class RepositoryHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public AppDatabaseContext Db { get; }
        public RecentlyViewedRepository Repository { get; }

        private RepositoryHarness(SqliteConnection connection, AppDatabaseContext db, RecentlyViewedRepository repository)
        {
            _connection = connection;
            Db = db;
            Repository = repository;
        }

        public static async Task<RepositoryHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AppDatabaseContext>()
                .UseSqlite(connection)
                .Options;

            var db = new AppDatabaseContext(options);
            await db.Database.EnsureCreatedAsync();

            db.Users.AddRange(
                new User { Id = 1, Email = "one@test.local", Name = "One", Usertype = "Participant" },
                new User { Id = 2, Email = "two@test.local", Name = "Two", Usertype = "Participant" });

            db.Clubs.Add(new Club
            {
                Id = 1,
                UserId = 1,
                Name = "Repository Club",
                Description = "Repository coverage club",
                Clubtype = ClubType.Gaming,
                ClubImage = "https://cdn.test/clubs/repository.png"
            });

            for (var id = 1; id <= 25; id++)
            {
                db.Events.Add(new backend.main.features.events.Events
                {
                    Id = id,
                    ClubId = 1,
                    Name = $"Event {id}",
                    Description = "An event used for recently viewed repository tests.",
                    Location = "Student Center",
                    LifecycleState = EventLifecycleState.Published,
                    StartTime = Now.AddDays(id),
                    EndTime = Now.AddDays(id).AddHours(2),
                    maxParticipants = 10,
                    registerCost = 0,
                    Category = EventCategory.Other
                });
            }

            await db.SaveChangesAsync();

            return new RepositoryHarness(connection, db, new RecentlyViewedRepository(db));
        }

        public async Task SeedAsync(params (int UserId, int EventId, DateTime ViewedAt)[] entries)
        {
            foreach (var entry in entries)
            {
                Db.RecentlyViewedEvents.Add(new RecentlyViewedEvent
                {
                    UserId = entry.UserId,
                    EventId = entry.EventId,
                    ViewedAt = entry.ViewedAt
                });
            }

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
