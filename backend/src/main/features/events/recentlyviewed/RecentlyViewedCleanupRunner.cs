using backend.main.infrastructure.database.core;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace backend.main.features.events.recentlyviewed;

/// <summary>
/// Collects history entries past the retention window.
/// <para>
/// Split from <see cref="RecentlyViewedCleanupService"/> so the actual work is a plain scoped
/// class: the timer loop is untestable by nature, but this is exercised directly with a fake
/// <see cref="TimeProvider"/>.
/// </para>
/// </summary>
public sealed class RecentlyViewedCleanupRunner
{
    private readonly AppDatabaseContext _db;
    private readonly RecentlyViewedOptions _options;
    private readonly TimeProvider _timeProvider;

    public RecentlyViewedCleanupRunner(
        AppDatabaseContext db,
        IOptions<RecentlyViewedOptions> options,
        TimeProvider timeProvider)
    {
        _db = db;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.PurgeEnabled || _options.PurgeBatchSize <= 0 || _options.RetentionDays <= 0)
            return;

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-_options.RetentionDays);

        // Bounded rather than "until empty": this runs on one scoped DbContext, and a large
        // backlog should not hold it open indefinitely. Whatever is left waits for the next pass.
        for (var batch = 0; batch < _options.MaxPurgeBatchesPerRun; batch++)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var ids = await _db.RecentlyViewedEvents
                .AsNoTracking()
                .Where(v => v.ViewedAt < cutoff)
                .OrderBy(v => v.ViewedAt)
                .Take(_options.PurgeBatchSize)
                .Select(v => v.Id)
                .ToListAsync(cancellationToken);

            if (ids.Count == 0)
                return;

            await _db.RecentlyViewedEvents
                .Where(v => ids.Contains(v.Id))
                .ExecuteDeleteAsync(cancellationToken);

            // A short batch means the backlog is drained; no point paying for another round trip.
            if (ids.Count < _options.PurgeBatchSize)
                return;
        }
    }
}
