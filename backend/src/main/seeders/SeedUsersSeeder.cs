using backend.main.features.profile;
using backend.main.infrastructure.database.core;

using Microsoft.EntityFrameworkCore;

namespace backend.main.seeders;

public sealed class SeedUsersSeeder : ISeeder
{
    private const string DefaultUserPassword = "Password123!";
    private readonly AppDatabaseContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SeedUsersSeeder> _logger;

    public SeedUsersSeeder(
        AppDatabaseContext dbContext,
        IConfiguration configuration,
        ILogger<SeedUsersSeeder> logger)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var desiredUsers = UserSeedCatalog.All;
        var desiredEmails = desiredUsers
            .Select(user => user.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredUsernames = desiredUsers
            .Select(user => UsernamePolicy.NormalizeAndValidate(user.Username))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingUsers = await _dbContext.Users
            .Where(user =>
                desiredEmails.Contains(user.Email)
                || (user.Username != null && desiredUsernames.Contains(user.Username)))
            .ToListAsync(cancellationToken);
        var existingByEmail = existingUsers.ToDictionary(user => user.Email, StringComparer.OrdinalIgnoreCase);
        var existingByUsername = existingUsers
            .Where(user => !string.IsNullOrWhiteSpace(user.Username))
            .ToDictionary(user => user.Username!, StringComparer.OrdinalIgnoreCase);

        var defaultPassword = ResolveDefaultUserPassword();
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(
            defaultPassword,
            workFactor: 12);

        var createdCount = 0;
        var updatedCount = 0;

        foreach (var definition in desiredUsers)
        {
            var normalizedUsername = UsernamePolicy.NormalizeAndValidate(definition.Username);
            if (existingByUsername.TryGetValue(normalizedUsername, out var usernameOwner)
                && !string.Equals(usernameOwner.Email, definition.Email, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Cannot seed '{definition.Email}': username '{definition.Username}' "
                    + $"is already assigned to '{usernameOwner.Email}'.");
            }

            if (existingByEmail.TryGetValue(definition.Email, out var existing))
            {
                if (ApplyDefinition(existing, definition, defaultPassword, passwordHash))
                    updatedCount++;

                continue;
            }

            _dbContext.Users.Add(CreateUser(definition, passwordHash));
            createdCount++;
        }

        if (createdCount == 0 && updatedCount == 0)
        {
            _logger.LogInformation("[Seeders] Seed users already match the thematic catalog.");
            return;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[Seeders] Reconciled seed users. Created {CreatedCount}, updated {UpdatedCount}.",
            createdCount,
            updatedCount);
    }

    /// <summary>Internal for tests: the display invariant here has no other seam to check.</summary>
    internal static User CreateUser(SeedUserDefinition definition, string passwordHash)
    {
        var now = DateTime.UtcNow;

        // Both forms from one UsernameForms value, so Normalize(display) == Username holds by
        // construction. This path inserts straight through the context rather than going via
        // AuthUserRepository.CreateUserAsync, so it does not get that method's display repair and
        // has to establish the invariant itself.
        var username = UsernamePolicy.NormalizeAndValidateWithDisplay(definition.Username);

        return new User
        {
            Email = definition.Email,
            Username = username.Username,
            UsernameDisplay = username.Display,
            Name = definition.Name,
            Usertype = definition.Role,
            Password = passwordHash,
            Avatar = null,
            Address = null,
            Phone = null,
            GoogleID = null,
            MicrosoftID = null,
            IsDisabled = false,
            DisabledAtUtc = null,
            DisabledReason = null,
            AuthVersion = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>Internal for tests: see <see cref="CreateUser"/>.</summary>
    internal static bool ApplyDefinition(
        User user,
        SeedUserDefinition definition,
        string defaultPassword,
        string passwordHash)
    {
        var changed = false;

        changed |= SetIfDifferent(user.Email, definition.Email, value => user.Email = value);

        // Both columns move together. Updating Username alone would leave the display form of the
        // previous name attached to the new one — for a seeded account that was renamed in-app, or
        // after a username edit in the catalog — and the next run would then trip
        // CK_Users_UsernameDisplay_Normalizes and abort seeding with a 23514.
        var username = UsernamePolicy.NormalizeAndValidateWithDisplay(definition.Username);
        changed |= SetIfDifferent(
            user.Username,
            username.Username,
            value => user.Username = value);
        changed |= SetIfDifferent(
            user.UsernameDisplay,
            username.Display,
            value => user.UsernameDisplay = value);
        changed |= SetIfDifferent(user.Name, definition.Name, value => user.Name = value);
        changed |= SetIfDifferent(user.Usertype, definition.Role, value => user.Usertype = value);
        if (string.IsNullOrWhiteSpace(user.Password)
            || !BCrypt.Net.BCrypt.Verify(defaultPassword, user.Password))
        {
            user.Password = passwordHash;
            changed = true;
        }

        changed |= SetNullable(user.Avatar, null, value => user.Avatar = value);
        changed |= SetNullable(user.Address, null, value => user.Address = value);
        changed |= SetNullable(user.Phone, null, value => user.Phone = value);
        changed |= SetNullable(user.GoogleID, null, value => user.GoogleID = value);
        changed |= SetNullable(user.MicrosoftID, null, value => user.MicrosoftID = value);

        if (user.IsDisabled)
        {
            user.IsDisabled = false;
            changed = true;
        }

        if (user.DisabledAtUtc != null)
        {
            user.DisabledAtUtc = null;
            changed = true;
        }

        if (user.DisabledReason != null)
        {
            user.DisabledReason = null;
            changed = true;
        }

        if (user.AuthVersion < 1)
        {
            user.AuthVersion = 1;
            changed = true;
        }

        if (changed)
            user.UpdatedAt = DateTime.UtcNow;

        return changed;
    }

    private string ResolveDefaultUserPassword()
    {
        return _configuration["AUTH_SEED_USERS_PASSWORD"]
            ?? _configuration["Seeders:AuthUsers:Password"]
            ?? DefaultUserPassword;
    }

    private static bool SetIfDifferent(string? currentValue, string nextValue, Action<string> setter)
    {
        if (string.Equals(currentValue, nextValue, StringComparison.Ordinal))
            return false;

        setter(nextValue);
        return true;
    }

    private static bool SetNullable(string? currentValue, string? nextValue, Action<string?> setter)
    {
        if (string.Equals(currentValue, nextValue, StringComparison.Ordinal))
            return false;

        setter(nextValue);
        return true;
    }
}
