using System.Data;

using backend.main.application.security;
using backend.main.features.auth.contracts;
using backend.main.features.profile;
using backend.main.features.profile.contracts;
using backend.main.infrastructure.database.core;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace backend.main.features.auth
{
    public class AuthUserRepository : IAuthUserRepository, IUserRepository
    {
        private readonly AppDatabaseContext _context;

        public AuthUserRepository(AppDatabaseContext context) => _context = context;

        public async Task<User> CreateUserAsync(User user)
        {
            user.Usertype = AuthRoles.NormalizeStored(user.Usertype);
            if (!string.IsNullOrWhiteSpace(user.Username))
                user.Username = UsernamePolicy.NormalizeAndValidate(user.Username);

            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable);
                try
                {
                    if (user.Username != null)
                    {
                        var reservation = await _context.UsernameReservations
                            .FindAsync(user.Username);
                        if (reservation?.ReservedUntilUtc > DateTime.UtcNow)
                            throw new UsernameTakenException(user.Username);

                        if (reservation != null)
                            _context.UsernameReservations.Remove(reservation);
                    }

                    await _context.Users.AddAsync(user);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return user;
                }
                // The availability check callers run before this happens outside the transaction,
                // so two signups can both see the same name as free and race to insert it. The
                // loser hits the unique index; without this it would surface as a 500 rather than
                // the 409 the caller already knows how to turn into "pick another name".
                catch (Exception exception)
                    when (user.Username != null && IsUsernameUniqueViolation(exception))
                {
                    await transaction.RollbackAsync();
                    _context.ChangeTracker.Clear();
                    throw new UsernameTakenException(user.Username);
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });
        }

        public async Task<User?> UpdateUserAsync(int id, User updated)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
                return null;

            user.Password = updated.Password ?? user.Password;
            user.Usertype = updated.Usertype != null
                ? AuthRoles.NormalizeStored(updated.Usertype)
                : user.Usertype;
            user.Name = updated.Name ?? user.Name;
            user.Avatar = updated.Avatar ?? user.Avatar;
            user.Address = updated.Address ?? user.Address;
            user.Phone = updated.Phone ?? user.Phone;

            await _context.SaveChangesAsync();
            return user;
        }

        public async Task<User?> UpdatePartialAsync(User updated)
        {
            var existing = await _context.Users.FindAsync(updated.Id);
            if (existing == null)
                return null;

            // Identity and role are intentionally NOT mutable through a partial update.
            // Email changes require re-verification and role changes go through dedicated
            // admin/status flows; otherwise a stale JWT claim could silently overwrite them.
            if (updated.Name != null)
                existing.Name = updated.Name;
            if (updated.Avatar != null)
                existing.Avatar = updated.Avatar;
            if (updated.Address != null)
                existing.Address = updated.Address;
            if (updated.Phone != null)
                existing.Phone = updated.Phone;

            await _context.SaveChangesAsync();
            return existing;
        }

        public async Task<UserOAuthRecord?> UpdateProviderIdsAsync(int id, string? googleId, string? microsoftId)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
                return null;

            if (googleId != null)
                user.GoogleID = googleId;
            if (microsoftId != null)
                user.MicrosoftID = microsoftId;

            await _context.SaveChangesAsync();
            return ToOAuthRecord(user);
        }

        public async Task<UserStatusRecord?> UpdateUserStatusAsync(int id, bool isDisabled, string? disabledReason)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
                return null;

            user.IsDisabled = isDisabled;
            user.DisabledAtUtc = isDisabled ? DateTime.UtcNow : null;
            user.DisabledReason = isDisabled ? disabledReason : null;
            user.AuthVersion += 1;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return new UserStatusRecord
            {
                Id = user.Id,
                IsDisabled = user.IsDisabled,
                DisabledAtUtc = user.DisabledAtUtc,
                DisabledReason = user.DisabledReason,
                AuthVersion = user.AuthVersion,
            };
        }

        public async Task<bool> IncrementAuthVersionAsync(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
                return false;

            user.AuthVersion += 1;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<IReadOnlyList<string>> DeleteUserAsync(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
                return Array.Empty<string>();

            // The context enables retry-on-failure, and that strategy rejects a
            // user-initiated transaction unless the whole unit runs through it.
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync();

                // Deleting the user cascades to their clubs, club versions, events and event
                // images (all DeleteBehavior.Cascade). EF only removes the rows, so the blob URLs
                // those rows carry become unrecoverable once the cascade runs. Gather them here,
                // before the delete, so the caller can clean up the orphaned blobs afterwards.
                var ownedClubIds = await _context.Clubs
                    .Where(club => club.UserId == id)
                    .Select(club => club.Id)
                    .ToListAsync();

                var orphanedBlobUrls = new List<string>();
                if (!string.IsNullOrEmpty(user.Avatar))
                    orphanedBlobUrls.Add(user.Avatar);

                if (ownedClubIds.Count > 0)
                {
                    orphanedBlobUrls.AddRange(await _context.Clubs
                        .Where(club => ownedClubIds.Contains(club.Id)
                            && club.ClubImage != null && club.ClubImage != string.Empty)
                        .Select(club => club.ClubImage!)
                        .ToListAsync());

                    orphanedBlobUrls.AddRange(await _context.ClubVersions
                        .Where(version => ownedClubIds.Contains(version.ClubId)
                            && version.ClubImage != null && version.ClubImage != string.Empty)
                        .Select(version => version.ClubImage!)
                        .ToListAsync());

                    var ownedEventIds = await _context.Events
                        .Where(ev => ownedClubIds.Contains(ev.ClubId))
                        .Select(ev => ev.Id)
                        .ToListAsync();

                    if (ownedEventIds.Count > 0)
                    {
                        orphanedBlobUrls.AddRange(await _context.EventImages
                            .Where(image => ownedEventIds.Contains(image.EventId)
                                && image.ImageUrl != null && image.ImageUrl != string.Empty)
                            .Select(image => image.ImageUrl!)
                            .ToListAsync());
                    }
                }

                // ClubStaff.GrantedByUserId is a Restrict FK, so staff roles this user granted to
                // others would block the delete. Reassign those grants to the club's owner (falling
                // back to the affected member) so the role survives and the account can be removed.
                var grantsByUser = await _context.ClubStaff
                    .Where(staff => staff.GrantedByUserId == id)
                    .ToListAsync();

                if (grantsByUser.Count > 0)
                {
                    var grantClubIds = grantsByUser.Select(staff => staff.ClubId).Distinct().ToList();
                    var clubOwners = await _context.Clubs
                        .Where(club => grantClubIds.Contains(club.Id))
                        .ToDictionaryAsync(club => club.Id, club => club.UserId);

                    foreach (var grant in grantsByUser)
                    {
                        var ownerId = clubOwners.TryGetValue(grant.ClubId, out var owner)
                            ? owner
                            : grant.UserId;
                        grant.GrantedByUserId = ownerId != id ? ownerId : grant.UserId;
                    }

                    await _context.SaveChangesAsync();
                }

                _context.Users.Remove(user);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();

                return orphanedBlobUrls
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            });
        }

        public async Task<User?> GetUserAsync(int id)
        {
            return await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == id)
                .Select(u => new User
                {
                    Id = u.Id,
                    Email = u.Email,
                    Password = null,
                    HasLocalPassword = u.Password != null,
                    Usertype = AuthRoles.NormalizeStored(u.Usertype),
                    Name = u.Name,
                    Username = u.Username,
                    UsernameChangeAvailableAtUtc = u.UsernameChangeAvailableAtUtc,
                    Avatar = u.Avatar,
                    Address = u.Address,
                    Phone = u.Phone,
                    MicrosoftID = u.MicrosoftID,
                    GoogleID = u.GoogleID,
                    IsDisabled = u.IsDisabled,
                    DisabledAtUtc = u.DisabledAtUtc,
                    DisabledReason = u.DisabledReason,
                    AuthVersion = u.AuthVersion,
                    CreatedAt = u.CreatedAt,
                    UpdatedAt = u.UpdatedAt,
                })
                .FirstOrDefaultAsync();
        }

        public async Task<UserAuthRecord?> GetAuthByUsernameAsync(string username)
        {
            return await GetAuthRecords()
                .Where(u => u.Username == username)
                .Select(u => new UserAuthRecord
                {
                    Id = u.Id,
                    Email = u.Email,
                    Password = u.Password,
                    Usertype = AuthRoles.NormalizeStored(u.Usertype),
                    Name = u.Name,
                    IsDisabled = u.IsDisabled,
                    AuthVersion = u.AuthVersion,
                })
                .FirstOrDefaultAsync();
        }

        public async Task<UserAuthRecord?> GetAuthByEmailAsync(string email)
        {
            return await GetAuthRecords()
                .Where(u => u.Email == email)
                .Select(u => new UserAuthRecord
                {
                    Id = u.Id,
                    Email = u.Email,
                    Password = u.Password,
                    Usertype = AuthRoles.NormalizeStored(u.Usertype),
                    Name = u.Name,
                    IsDisabled = u.IsDisabled,
                    AuthVersion = u.AuthVersion,
                })
                .FirstOrDefaultAsync();
        }

        public async Task<UserAuthRecord?> GetAuthByIdAsync(int id)
        {
            return await GetAuthRecords()
                .Where(u => u.Id == id)
                .Select(u => new UserAuthRecord
                {
                    Id = u.Id,
                    Email = u.Email,
                    Password = u.Password,
                    Usertype = AuthRoles.NormalizeStored(u.Usertype),
                    Name = u.Name,
                    IsDisabled = u.IsDisabled,
                    AuthVersion = u.AuthVersion,
                })
                .FirstOrDefaultAsync();
        }

        public Task<UserRecoveryRecord?> GetRecoveryByUsernameAsync(string username) =>
            GetRecoveryRecords()
                .Where(u => u.Username == username)
                .FirstOrDefaultAsync();

        public Task<UserRecoveryRecord?> GetRecoveryByEmailAsync(string email) =>
            GetRecoveryRecords()
                .Where(u => u.Email == email)
                .FirstOrDefaultAsync();

        private IQueryable<UserRecoveryRecord> GetRecoveryRecords() =>
            _context.Users
                .AsNoTracking()
                .Select(u => new UserRecoveryRecord
                {
                    Id = u.Id,
                    Email = u.Email,
                    Username = u.Username,
                    RecipientName = u.Name,
                    IsDisabled = u.IsDisabled,
                    HasLocalPassword = u.Password != null,
                    HasGoogleProvider = u.GoogleID != null,
                    HasMicrosoftProvider = u.MicrosoftID != null,
                });

        private IQueryable<User> GetAuthRecords() => _context.Users.AsNoTracking();

        public async Task<UserOAuthRecord?> GetOAuthByEmailAsync(string email)
        {
            return await _context.Users
                .AsNoTracking()
                .Where(u => u.Email == email)
                .Select(u => new UserOAuthRecord
                {
                    Id = u.Id,
                    Email = u.Email,
                    Usertype = AuthRoles.NormalizeStored(u.Usertype),
                    GoogleID = u.GoogleID,
                    MicrosoftID = u.MicrosoftID,
                    IsDisabled = u.IsDisabled,
                    AuthVersion = u.AuthVersion,
                })
                .FirstOrDefaultAsync();
        }

        public async Task<UserOAuthRecord?> GetOAuthByMicrosoftIdAsync(string microsoftId)
        {
            return await _context.Users
                .AsNoTracking()
                .Where(u => u.MicrosoftID == microsoftId)
                .Select(u => new UserOAuthRecord
                {
                    Id = u.Id,
                    Email = u.Email,
                    Usertype = AuthRoles.NormalizeStored(u.Usertype),
                    GoogleID = u.GoogleID,
                    MicrosoftID = u.MicrosoftID,
                    IsDisabled = u.IsDisabled,
                    AuthVersion = u.AuthVersion,
                })
                .FirstOrDefaultAsync();
        }

        public async Task<UserOAuthRecord?> GetOAuthByGoogleIdAsync(string googleId)
        {
            return await _context.Users
                .AsNoTracking()
                .Where(u => u.GoogleID == googleId)
                .Select(u => new UserOAuthRecord
                {
                    Id = u.Id,
                    Email = u.Email,
                    Usertype = AuthRoles.NormalizeStored(u.Usertype),
                    GoogleID = u.GoogleID,
                    MicrosoftID = u.MicrosoftID,
                    IsDisabled = u.IsDisabled,
                    AuthVersion = u.AuthVersion,
                })
                .FirstOrDefaultAsync();
        }

        public async Task<UserProfileRecord?> GetProfileByUsernameAsync(string username)
        {
            return await _context.Users
                .AsNoTracking()
                .Where(u => u.Username == username)
                .Select(u => new UserProfileRecord
                {
                    Id = u.Id,
                    Email = u.Email,
                    Username = string.IsNullOrWhiteSpace(u.Username) ? u.Email : u.Username,
                    Name = u.Name,
                    Avatar = u.Avatar,
                    Usertype = AuthRoles.NormalizeStored(u.Usertype),
                    CreatedAtUtc = u.CreatedAt,
                })
                .FirstOrDefaultAsync();
        }

        public async Task<UserProfileRecord?> GetPublicProfileByUsernameOrReservationAsync(
            string username,
            DateTime utcNow)
        {
            var direct = await GetProfileByUsernameAsync(username);
            if (direct != null)
                return direct;

            var userId = await _context.UsernameReservations
                .AsNoTracking()
                .Where(reservation =>
                    reservation.Username == username
                    && reservation.ReservedUntilUtc > utcNow)
                .Select(reservation => (int?)reservation.UserId)
                .FirstOrDefaultAsync();

            if (userId == null)
                return null;

            return await _context.Users
                .AsNoTracking()
                .Where(user => user.Id == userId.Value)
                .Select(user => new UserProfileRecord
                {
                    Id = user.Id,
                    Email = user.Email,
                    Username = string.IsNullOrWhiteSpace(user.Username) ? user.Email : user.Username,
                    Name = user.Name,
                    Avatar = user.Avatar,
                    Usertype = AuthRoles.NormalizeStored(user.Usertype),
                    CreatedAtUtc = user.CreatedAt,
                })
                .FirstOrDefaultAsync();
        }

        public async Task<UserProfileRecord?> GetProfileByEmailAsync(string email)
        {
            return await _context.Users
                .AsNoTracking()
                .Where(u => u.Email == email)
                .Select(u => new UserProfileRecord
                {
                    Id = u.Id,
                    Email = u.Email,
                    Username = string.IsNullOrWhiteSpace(u.Username) ? u.Email : u.Username,
                    Name = u.Name,
                    Avatar = u.Avatar,
                    Usertype = AuthRoles.NormalizeStored(u.Usertype),
                    CreatedAtUtc = u.CreatedAt,
                })
                .FirstOrDefaultAsync();
        }

        public async Task<IReadOnlyList<UserListRecord>> GetUsersAsync(
            string? role = null,
            UserReadDetailLevel detail = UserReadDetailLevel.Slim
        )
        {
            var query = _context.Users
                .AsNoTracking()
                .AsQueryable();

            if (!string.IsNullOrEmpty(role))
            {
                var normalizedRole = AuthRoles.NormalizeStored(role);
                query = query.Where(u => u.Usertype == normalizedRole);
            }

            return await ProjectUserListQuery(query, detail).ToListAsync();
        }

        public async Task<bool> EmailExistsAsync(string email)
        {
            return await _context.Users
                .AsNoTracking()
                .AnyAsync(u => u.Email == email);
        }

        public async Task<bool> UsernameExistsAsync(string username, int excludeUserId)
        {
            return await _context.Users
                .AsNoTracking()
                .AnyAsync(u => u.Username == username && u.Id != excludeUserId);
        }

        public async Task<bool> UsernameUnavailableAsync(string username, DateTime utcNow)
        {
            if (await _context.Users.AsNoTracking().AnyAsync(user => user.Username == username))
                return true;

            return await _context.UsernameReservations
                .AsNoTracking()
                .AnyAsync(reservation =>
                    reservation.Username == username
                    && reservation.ReservedUntilUtc > utcNow);
        }

        public async Task<UsernameChangeRecord> ChangeUsernameAsync(
            int userId,
            string username,
            DateTime utcNow,
            DateTime reservedUntilUtc)
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable);
                try
                {
                    // PostgreSQL's row lock serializes changes for the same account. The
                    // serializable transaction also protects the cross-table namespace check for
                    // usernames that do not yet have a row to lock.
                    var user = _context.Database.IsNpgsql()
                        ? await _context.Users
                            .FromSqlInterpolated(
                                $"SELECT * FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE")
                            .SingleOrDefaultAsync()
                        : await _context.Users.FindAsync(userId);
                    if (user == null)
                        return new UsernameChangeRecord(UsernameChangeStatus.UserNotFound);

                    // The name being replaced, so Normalize rather than NormalizeAndValidate: it
                    // may predate the format rules, and validating it here would leave the owner
                    // permanently unable to rename away from it.
                    var currentUsername = string.IsNullOrWhiteSpace(user.Username)
                        ? null
                        : UsernamePolicy.Normalize(user.Username);

                    if (currentUsername == username)
                        return new UsernameChangeRecord(UsernameChangeStatus.Unchanged, user);

                    if (currentUsername != null
                        && user.UsernameChangeAvailableAtUtc is DateTime availableAtUtc
                        && availableAtUtc > utcNow)
                    {
                        return new UsernameChangeRecord(
                            UsernameChangeStatus.CooldownActive,
                            user,
                            availableAtUtc);
                    }

                    if (await _context.Users
                        .AsNoTracking()
                        .AnyAsync(other => other.Id != userId && other.Username == username))
                    {
                        return new UsernameChangeRecord(UsernameChangeStatus.Unavailable, user);
                    }

                    var requestedReservation = await _context.UsernameReservations
                        .FindAsync(username);
                    if (requestedReservation != null)
                    {
                        if (requestedReservation.ReservedUntilUtc > utcNow)
                            return new UsernameChangeRecord(UsernameChangeStatus.Unavailable, user);

                        _context.UsernameReservations.Remove(requestedReservation);
                    }

                    if (currentUsername != null)
                    {
                        var oldReservation = await _context.UsernameReservations
                            .FindAsync(currentUsername);
                        if (oldReservation == null)
                        {
                            _context.UsernameReservations.Add(new UsernameReservation
                            {
                                Username = currentUsername,
                                UserId = userId,
                                ReservedUntilUtc = reservedUntilUtc,
                            });
                        }
                        else
                        {
                            oldReservation.UserId = userId;
                            oldReservation.ReservedUntilUtc = reservedUntilUtc;
                        }
                    }

                    user.Username = username;
                    user.UsernameChangeAvailableAtUtc = currentUsername == null
                        ? null
                        : reservedUntilUtc;
                    user.UpdatedAt = utcNow;

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return new UsernameChangeRecord(
                        UsernameChangeStatus.Changed,
                        user,
                        PreviousUsername: currentUsername);
                }
                catch (Exception exception) when (IsWriteConflict(exception))
                {
                    await transaction.RollbackAsync();
                    _context.ChangeTracker.Clear();
                    return new UsernameChangeRecord(UsernameChangeStatus.Unavailable);
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });
        }

        public async Task<EmailChangeRecord> ChangeEmailAsync(
            int userId,
            string email,
            int expectedAuthVersion,
            DateTime utcNow)
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable);
                try
                {
                    // Same row lock as ChangeUsernameAsync: it serializes concurrent changes to
                    // this account, while the serializable transaction covers the uniqueness check
                    // against an address that has no row of its own to lock.
                    var user = _context.Database.IsNpgsql()
                        ? await _context.Users
                            .FromSqlInterpolated(
                                $"SELECT * FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE")
                            .SingleOrDefaultAsync()
                        : await _context.Users.FindAsync(userId);
                    if (user == null)
                        return new EmailChangeRecord(EmailChangeStatus.UserNotFound);

                    // Verified here rather than by the caller: the row is locked, so this is the
                    // only place the version can be compared without a window in which a password
                    // change could commit between the check and the write.
                    if (user.AuthVersion != expectedAuthVersion)
                        return new EmailChangeRecord(EmailChangeStatus.Stale, user);

                    var previousEmail = user.Email;

                    // Email is a citext column, so the database compares case-insensitively and
                    // this normalised comparison matches what the unique index would enforce.
                    if (EmailPolicy.Normalize(previousEmail) == EmailPolicy.Normalize(email))
                        return new EmailChangeRecord(EmailChangeStatus.Unchanged, user);

                    if (await _context.Users
                        .AsNoTracking()
                        .AnyAsync(other => other.Id != userId && other.Email == email))
                    {
                        return new EmailChangeRecord(EmailChangeStatus.Unavailable, user);
                    }

                    user.Email = email;
                    // Bumped in the same SaveChanges as the address itself: the email is an access
                    // token claim, so a change that landed without invalidating outstanding tokens
                    // would leave live sessions authenticating as an address the account no longer
                    // owns. See JwtConfiguration.OnTokenValidated.
                    user.AuthVersion += 1;
                    user.UpdatedAt = utcNow;

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return new EmailChangeRecord(
                        EmailChangeStatus.Changed,
                        user,
                        PreviousEmail: previousEmail);
                }
                catch (Exception exception) when (IsWriteConflict(exception))
                {
                    await transaction.RollbackAsync();
                    _context.ChangeTracker.Clear();
                    return new EmailChangeRecord(EmailChangeStatus.Unavailable);
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });
        }

        /// <summary>Name EF gives the unique index on <c>Users.Username</c>.</summary>
        private const string UsernameUniqueIndexName = "IX_Users_Username";

        /// <summary>
        /// Whether a failed write lost the race on the unique username index specifically.
        /// </summary>
        /// <remarks>
        /// Deliberately narrower than <see cref="IsWriteConflict"/>, which answers "lost a race on
        /// something" for any constraint. <see cref="CreateUserAsync"/> can equally collide on
        /// Email, GoogleID or MicrosoftID, and reporting one of those as a taken username would
        /// send the caller off to change the wrong field. Postgres names the index it violated;
        /// SQLite, used by the repository tests, only names the columns in its message.
        /// </remarks>
        private static bool IsUsernameUniqueViolation(Exception exception)
        {
            var databaseException = exception is DbUpdateException
                ? exception.InnerException
                : exception;

            return databaseException switch
            {
                PostgresException postgres =>
                    postgres.SqlState == PostgresErrorCodes.UniqueViolation
                    && string.Equals(
                        postgres.ConstraintName,
                        UsernameUniqueIndexName,
                        StringComparison.Ordinal),
                SqliteException { SqliteErrorCode: 19 } sqlite =>
                    sqlite.Message.Contains("Users.Username", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        private static bool IsWriteConflict(Exception exception)
        {
            var databaseException = exception is DbUpdateException
                ? exception.InnerException
                : exception;

            return databaseException is PostgresException postgresException
                    && postgresException.SqlState is
                        PostgresErrorCodes.UniqueViolation
                        or PostgresErrorCodes.SerializationFailure
                        or PostgresErrorCodes.DeadlockDetected
                || databaseException is SqliteException { SqliteErrorCode: 19 };
        }

        public async Task<IReadOnlyList<UserListRecord>> GetByIdsAsync(
            IEnumerable<int> ids,
            UserReadDetailLevel detail = UserReadDetailLevel.Slim
        )
        {
            var idList = ids.Distinct().ToList();

            if (idList.Count == 0)
                return [];

            var users = await ProjectUserListQuery(
                    _context.Users
                        .AsNoTracking()
                        .Where(u => idList.Contains(u.Id)),
                    detail
                )
                .ToListAsync();

            return idList
                .Select(id => users.FirstOrDefault(u => u.Id == id))
                .Where(user => user != null)
                .Cast<UserListRecord>()
                .ToList();
        }

        private static IQueryable<UserListRecord> ProjectUserListQuery(
            IQueryable<User> query,
            UserReadDetailLevel detail
        )
        {
            if (detail == UserReadDetailLevel.Admin)
            {
                return query.Select(u => new UserListRecord
                {
                    Id = u.Id,
                    Email = u.Email,
                    Username = string.IsNullOrWhiteSpace(u.Username) ? u.Email : u.Username,
                    Name = u.Name,
                    Avatar = u.Avatar,
                    Usertype = AuthRoles.NormalizeStored(u.Usertype),
                    IsDisabled = u.IsDisabled,
                    DisabledAtUtc = u.DisabledAtUtc,
                    DisabledReason = u.DisabledReason,
                    CreatedAt = u.CreatedAt,
                    UpdatedAt = u.UpdatedAt,
                });
            }

            return query.Select(u => new UserListRecord
            {
                Id = u.Id,
                Email = u.Email,
                Username = string.IsNullOrWhiteSpace(u.Username) ? u.Email : u.Username,
                Name = u.Name,
                Avatar = u.Avatar,
                Usertype = AuthRoles.NormalizeStored(u.Usertype),
                IsDisabled = null,
                DisabledAtUtc = null,
                DisabledReason = null,
                CreatedAt = null,
                UpdatedAt = null,
            });
        }

        private static UserOAuthRecord ToOAuthRecord(User user)
        {
            return new UserOAuthRecord
            {
                Id = user.Id,
                Email = user.Email,
                Usertype = AuthRoles.NormalizeStored(user.Usertype),
                GoogleID = user.GoogleID,
                MicrosoftID = user.MicrosoftID,
                IsDisabled = user.IsDisabled,
                AuthVersion = user.AuthVersion,
            };
        }
    }
}
