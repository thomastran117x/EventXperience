using backend.main.features.clubs;
using backend.main.features.events;
using backend.main.features.events.recentlyviewed;
using backend.main.features.profile;
using backend.main.infrastructure.database.core;

using FluentAssertions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace backend.tests.Unit.Features.Events.RecentlyViewed;

public class RecentlyViewedCleanupRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunOnceAsync_ShouldDeleteOnlyEntriesPastRetention()
    {
        await using var harness = await CleanupHarness.CreateAsync();

        await harness.SeedAsync(
            (1, Now.UtcDateTime.AddDays(-91)),
            (2, Now.UtcDateTime.AddDays(-89)),
            (3, Now.UtcDateTime.AddDays(-200)));

        await harness.Runner.RunOnceAsync();

        var remaining = await harness.Db.RecentlyViewedEvents.AsNoTracking()
            .Select(v => v.EventId)
            .ToListAsync();
        remaining.Should().Equal(2);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldDoNothing_WhenPurgingIsDisabled()
    {
        await using var harness = await CleanupHarness.CreateAsync(new RecentlyViewedOptions { PurgeEnabled = false });

        await harness.SeedAsync((1, Now.UtcDateTime.AddDays(-200)));

        await harness.Runner.RunOnceAsync();

        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldDoNothing_WhenRetentionIsNotPositive()
    {
        await using var harness = await CleanupHarness.CreateAsync(new RecentlyViewedOptions { RetentionDays = 0 });

        await harness.SeedAsync((1, Now.UtcDateTime.AddDays(-200)));

        // A zero retention window would otherwise mean "expire everything", which is not what an
        // unset or mistyped configuration value should do.
        await harness.Runner.RunOnceAsync();

        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldDoNothing_WhenBatchSizeIsNotPositive()
    {
        await using var harness = await CleanupHarness.CreateAsync(new RecentlyViewedOptions { PurgeBatchSize = 0 });

        await harness.SeedAsync((1, Now.UtcDateTime.AddDays(-200)));

        await harness.Runner.RunOnceAsync();

        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldStopAtTheBatchBudget()
    {
        var options = new RecentlyViewedOptions { PurgeBatchSize = 2, MaxPurgeBatchesPerRun = 2 };
        await using var harness = await CleanupHarness.CreateAsync(options);

        var expired = Enumerable.Range(1, 10)
            .Select(id => (id, Now.UtcDateTime.AddDays(-100 - id)))
            .ToArray();
        await harness.SeedAsync(expired);

        await harness.Runner.RunOnceAsync();

        // One scoped DbContext must not be held open for an unbounded backlog; the rest waits for
        // the next pass.
        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(6);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldDrainInOneRun_WhenTheBacklogFitsTheBudget()
    {
        var options = new RecentlyViewedOptions { PurgeBatchSize = 4, MaxPurgeBatchesPerRun = 10 };
        await using var harness = await CleanupHarness.CreateAsync(options);

        var expired = Enumerable.Range(1, 10)
            .Select(id => (id, Now.UtcDateTime.AddDays(-100 - id)))
            .ToArray();
        await harness.SeedAsync(expired);

        await harness.Runner.RunOnceAsync();

        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldStop_WhenCancelled()
    {
        await using var harness = await CleanupHarness.CreateAsync();

        await harness.SeedAsync((1, Now.UtcDateTime.AddDays(-200)));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await harness.Runner.RunOnceAsync(cancelled.Token);

        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldSucceed_WhenThereIsNothingToPurge()
    {
        await using var harness = await CleanupHarness.CreateAsync();

        await harness.SeedAsync((1, Now.UtcDateTime.AddDays(-1)));

        await harness.Runner.RunOnceAsync();

        (await harness.Db.RecentlyViewedEvents.CountAsync()).Should().Be(1);
    }

    private sealed class CleanupHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public AppDatabaseContext Db { get; }
        public RecentlyViewedCleanupRunner Runner { get; }

        private CleanupHarness(SqliteConnection connection, AppDatabaseContext db, RecentlyViewedCleanupRunner runner)
        {
            _connection = connection;
            Db = db;
            Runner = runner;
        }

        public static async Task<CleanupHarness> CreateAsync(RecentlyViewedOptions? options = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var contextOptions = new DbContextOptionsBuilder<AppDatabaseContext>()
                .UseSqlite(connection)
                .Options;

            var db = new AppDatabaseContext(contextOptions);
            await db.Database.EnsureCreatedAsync();

            db.Users.Add(new User { Id = 1, Email = "sweeper@test.local", Name = "Sweeper", Usertype = "Participant" });
            db.Clubs.Add(new Club
            {
                Id = 1,
                UserId = 1,
                Name = "Cleanup Club",
                Description = "Cleanup coverage club",
                Clubtype = ClubType.Gaming,
                ClubImage = "https://cdn.test/clubs/cleanup.png"
            });

            for (var id = 1; id <= 20; id++)
            {
                db.Events.Add(new backend.main.features.events.Events
                {
                    Id = id,
                    ClubId = 1,
                    Name = $"Event {id}",
                    Description = "An event used for cleanup runner tests.",
                    Location = "Student Center",
                    LifecycleState = EventLifecycleState.Published,
                    StartTime = Now.UtcDateTime.AddDays(id),
                    EndTime = Now.UtcDateTime.AddDays(id).AddHours(2),
                    maxParticipants = 10,
                    registerCost = 0,
                    Category = EventCategory.Other
                });
            }

            await db.SaveChangesAsync();

            var runner = new RecentlyViewedCleanupRunner(
                db,
                Options.Create(options ?? new RecentlyViewedOptions()),
                new FakeTimeProvider(Now));

            return new CleanupHarness(connection, db, runner);
        }

        public async Task SeedAsync(params (int EventId, DateTime ViewedAt)[] entries)
        {
            foreach (var entry in entries)
            {
                Db.RecentlyViewedEvents.Add(new RecentlyViewedEvent
                {
                    UserId = 1,
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
