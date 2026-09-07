using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Timeback.Application.Abstractions;
using Timeback.Infrastructure.Persistence;

namespace Timeback.Infrastructure.MarketData;

internal sealed class EfInflationStore(TimebackDbContext db, IMemoryCache cache) : IInflationStore
{
    public async Task<decimal> GetIndexAsync(DateOnly date, CancellationToken ct)
    {
        var all = await cache.GetOrCreateAsync("inflation:all", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
            return await db.InflationIndices.AsNoTracking().OrderBy(x => x.Month).ToListAsync(ct);
        }) ?? [];

        if (all.Count == 0) return 1m;

        var month = new DateOnly(date.Year, date.Month, 1);
        // Exact month, else nearest earlier, else earliest available.
        return all.LastOrDefault(x => x.Month <= month)?.Index ?? all[0].Index;
    }
}
