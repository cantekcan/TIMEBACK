using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Timeback.Application.Abstractions;
using Timeback.Domain.Assets;
using Timeback.Domain.Scoring;
using Timeback.Infrastructure.Persistence;

namespace Timeback.Infrastructure.MarketData;

/// <summary>
/// Reads the immutable historical dataset from Postgres and memoises the whole thing. The cache is
/// keyed by content that never changes during a run, so there is no invalidation problem; swapping
/// <see cref="IMemoryCache"/> for a distributed Redis cache is a DI-only change.
/// </summary>
internal sealed class EfMarketDataStore(TimebackDbContext db, IMemoryCache cache) : IMarketDataStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    public Task<IReadOnlyList<Asset>> GetActiveAssetsAsync(CancellationToken ct) =>
        cache.GetOrCreateAsync<IReadOnlyList<Asset>>("assets:active", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return await db.Assets.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Symbol).ToListAsync(ct);
        })!;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<PricePoint>>> GetPriceSeriesAsync(CancellationToken ct)
    {
        var symbols = (await GetActiveAssetsAsync(ct)).Select(a => a.Symbol).ToHashSet();
        return (await cache.GetOrCreateAsync("prices:series", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            var rows = await db.DailyPrices.AsNoTracking()
                .OrderBy(p => p.Date)
                .Select(p => new { p.Symbol, p.Date, p.Close })
                .ToListAsync(ct);
            return rows
                .GroupBy(r => r.Symbol)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<PricePoint>)g.Select(r => new PricePoint(r.Date, r.Close)).ToList());
        }))!
        .Where(kv => symbols.Contains(kv.Key))
        .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public async Task<(DateOnly Earliest, DateOnly Latest)> GetCoverageAsync(CancellationToken ct)
    {
        var series = await GetPriceSeriesAsync(ct);
        if (series.Count == 0) return (DateOnly.MinValue, DateOnly.MinValue);
        return (series.Values.Max(v => v[0].Date), series.Values.Min(v => v[^1].Date));
    }

    public async Task<DateOnly?> ResolveEffectiveMarketDateAsync(DateOnly requested, CancellationToken ct)
    {
        var series = (await GetPriceSeriesAsync(ct)).Values
            .Select(v => v.Select(p => p.Date).ToHashSet())
            .ToList();
        if (series.Count == 0) return null;

        // All candidate dates at or before `requested`, newest first; return the first where every asset has a point.
        var candidateDates = series
            .SelectMany(s => s.Where(d => d <= requested))
            .Distinct()
            .OrderByDescending(d => d);

        foreach (var d in candidateDates)
            if (series.All(s => s.Contains(d)))
                return d;
        return null;
    }

    public async Task<IReadOnlyDictionary<string, AssetQuote>> GetQuotesAsync(
        DateOnly effectiveDate, DateOnly valuationDate, CancellationToken ct)
    {
        var series = await GetPriceSeriesAsync(ct);
        var quotes = new Dictionary<string, AssetQuote>();

        foreach (var (symbol, points) in series)
        {
            var then = PriceAtOrBefore(points, effectiveDate);
            var now = PriceAtOrBefore(points, valuationDate);
            if (then is > 0 && now is > 0)
                quotes[symbol] = new AssetQuote(symbol, then.Value, now.Value);
        }
        return quotes;
    }

    private static decimal? PriceAtOrBefore(IReadOnlyList<PricePoint> points, DateOnly date)
    {
        decimal? result = null;
        foreach (var p in points)
        {
            if (p.Date > date) break;
            result = p.Close;
        }
        return result;
    }
}
