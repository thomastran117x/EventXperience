using backend.main.features.bloom;
using backend.main.features.profile;
using backend.main.infrastructure.database.core;

using FluentAssertions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace backend.tests.Unit.Features.Bloom;

public class EmailBloomFilterSourceTests
{
    [Fact]
    public void Target_ShouldBeTheEmailFilter()
    {
        new EmailBloomFilterSource(null!).Target.Should().Be(BloomFilterTargets.Email);
    }

    [Fact]
    public async Task EnumerateAsync_ShouldYieldEveryAccountEmail()
    {
        await using var harness = await SourceHarness.CreateAsync();
        await harness.AddUserAsync("ada@example.com", "ada");
        await harness.AddUserAsync("grace@example.com", "grace");

        var values = await harness.EnumerateAsync();

        values.Should().BeEquivalentTo(["ada@example.com", "grace@example.com"]);
    }

    /// <summary>
    /// OAuth accounts carry an email but no username, so they are invisible to the username
    /// source. The email filter must still cover them or signup would report a provider-created
    /// address as free.
    /// </summary>
    [Fact]
    public async Task EnumerateAsync_ShouldIncludeAccountsWithoutAUsername()
    {
        await using var harness = await SourceHarness.CreateAsync();
        await harness.AddUserAsync("oauth@example.com", username: null);

        var values = await harness.EnumerateAsync();

        values.Should().BeEquivalentTo(["oauth@example.com"]);
    }

    /// <summary>
    /// The column is citext, so it stores whatever casing the account was created with. The filter
    /// hashes the literal string, so streaming raw values would seed bits no probe looks at.
    /// </summary>
    [Fact]
    public async Task EnumerateAsync_ShouldNormaliseValues_SoTheyMatchLookups()
    {
        await using var harness = await SourceHarness.CreateAsync();
        await harness.AddUserAsync("  Ada.Lovelace@Example.COM  ", "ada");

        var values = await harness.EnumerateAsync();

        values.Should().BeEquivalentTo(["ada.lovelace@example.com"]);
        values.Should().BeEquivalentTo(values.Select(EmailPolicy.Normalize));
    }

    [Fact]
    public async Task EnumerateAsync_ShouldReturnNothing_ForAnEmptyDatabase()
    {
        await using var harness = await SourceHarness.CreateAsync();

        (await harness.EnumerateAsync()).Should().BeEmpty();
    }

    private sealed class SourceHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly AppDatabaseContext _db;
        private int _nextUserId = 1;

        private SourceHarness(SqliteConnection connection, AppDatabaseContext db)
        {
            _connection = connection;
            _db = db;
            Source = new EmailBloomFilterSource(db);
        }

        public EmailBloomFilterSource Source { get; }

        public static async Task<SourceHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AppDatabaseContext>()
                .UseSqlite(connection)
                .Options;

            var db = new AppDatabaseContext(options);
            await db.Database.EnsureCreatedAsync();

            return new SourceHarness(connection, db);
        }

        public async Task AddUserAsync(string email, string? username)
        {
            _db.Users.Add(new User
            {
                Id = _nextUserId++,
                Email = email,
                Password = "hashed",
                Usertype = "participant",
                Username = username,
            });

            await _db.SaveChangesAsync();
        }

        public async Task<List<string>> EnumerateAsync()
        {
            var values = new List<string>();
            await foreach (var value in Source.EnumerateAsync(CancellationToken.None))
                values.Add(value);

            return values;
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
