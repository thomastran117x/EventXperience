using backend.main.shared.utilities.logger;

namespace backend.main.features.events.recentlyviewed;

/// <summary>
/// Runs the history expiry sweep on a timer.
/// <para>
/// Six-hourly rather than the hourly cadence the club-version cleanup uses: retention is measured
/// in months, so anything finer is churn. The read path filters by the same cutoff, so entries are
/// never presented in the window between ageing out and being collected.
/// </para>
/// <para>
/// Not leader-elected, so every replica sweeps. The deletes are idempotent, which makes that safe
/// if slightly wasteful - the same trade the existing cleanup services already make.
/// </para>
/// </summary>
public sealed class RecentlyViewedCleanupService : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    private readonly IServiceProvider _services;

    public RecentlyViewedCleanupService(IServiceProvider services)
    {
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<RecentlyViewedCleanupRunner>();
                await runner.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "[RecentlyViewedCleanupService] Failed to purge expired recently viewed events.");
            }

            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
