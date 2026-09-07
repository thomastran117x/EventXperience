using System.Runtime.CompilerServices;

using backend.main.features.profile;
using backend.main.infrastructure.database.core;

using Microsoft.EntityFrameworkCore;

namespace backend.main.features.bloom;

/// <summary>
/// Populates the email filter from the one table that owns the email namespace.
/// </summary>
/// <remarks>
/// Simpler than the username source: an address is registered exactly when a user row holds it,
/// which is the same predicate <c>AuthUserRepository.EmailExistsAsync</c> evaluates. There is no
/// cooldown table to union in, so this source needs no clock.
///
/// Every value is normalised through <see cref="EmailPolicy"/> because the read path is. The
/// column is <c>citext</c> and therefore stores whatever casing the account was created with, so
/// streaming the raw values would seed bits no probe ever looks at.
/// </remarks>
public sealed class EmailBloomFilterSource : IBloomFilterSource
{
    private readonly AppDatabaseContext _context;

    public EmailBloomFilterSource(AppDatabaseContext context)
    {
        _context = context;
    }

    public string Target => BloomFilterTargets.Email;

    public async IAsyncEnumerable<string> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var emails = _context.Users
            .AsNoTracking()
            .Select(user => user.Email)
            .AsAsyncEnumerable();

        await foreach (var email in emails.WithCancellation(cancellationToken))
            yield return EmailPolicy.Normalize(email);
    }
}
