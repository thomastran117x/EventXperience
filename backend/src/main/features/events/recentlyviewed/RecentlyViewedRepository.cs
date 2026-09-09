using backend.main.infrastructure.database.core;

using Microsoft.EntityFrameworkCore;

namespace backend.main.features.events.recentlyviewed
{
    public class RecentlyViewedRepository : IRecentlyViewedRepository
    {
        private readonly AppDatabaseContext _context;

        public RecentlyViewedRepository(AppDatabaseContext context) => _context = context;

        public async Task<IReadOnlyList<RecentlyViewedEvent>> GetRecentAsync(int userId, DateTime cutoff, int limit)
        {
            if (limit <= 0)
                return [];

            return await _context.RecentlyViewedEvents
                .AsNoTracking()
                .Where(v => v.UserId == userId && v.ViewedAt >= cutoff)
                // Id descending breaks ViewedAt ties the same way the write-side trim does, so
                // the rows presented are exactly the rows the trim decided to keep.
                .OrderByDescending(v => v.ViewedAt)
                .ThenByDescending(v => v.Id)
                .Take(limit)
                .ToListAsync();
        }

        public async Task<RecentlyViewedSetting?> GetSettingAsync(int userId)
        {
            return await _context.RecentlyViewedSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.UserId == userId);
        }
    }
}
