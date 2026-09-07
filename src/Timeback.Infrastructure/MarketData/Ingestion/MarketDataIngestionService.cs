using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Timeback.Application.Abstractions;
using Timeback.Domain.MarketData;
using Timeback.Infrastructure.Persistence;

namespace Timeback.Infrastructure.MarketData.Ingestion;

public sealed record IngestionReport(
    int AssetsProcessed,
    int PricesInserted,
    int PricesSkippedExisting,
    int InflationInserted,
    int InflationSkippedExisting,
    IReadOnlyList<string> Notes)
{
    public static IngestionReport Empty { get; } = new(0, 0, 0, 0, 0, []);
}

/// <summary>
/// Provider → normalise (forward-fill gaps, convert every quote to TRY using the USD/TRY series) →
/// idempotent upsert into PostgreSQL. The game engine only ever reads the result.
/// Only one implementation exists and none is ever swapped in tests, so this is a concrete class,
/// not an interface - DatabaseSeeder and the `ingest` CLI command both take it directly.
/// </summary>
public sealed class MarketDataIngestionService(
    TimebackDbContext db,
    IMarketDataProvider marketProvider,
    IInflationDataProvider inflationProvider,
    ILogger<MarketDataIngestionService> logger)
{
    public async Task<IngestionReport> IngestAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        from = new DateOnly(from.Year, from.Month, 1);
        to = new DateOnly(to.Year, to.Month, 1);
        var months = MonthRange(from, to).ToList();
        var notes = new List<string>();

        // 1. USD/TRY series is the FX backbone for every USD-quoted asset. Forward-fill only up to its
        // own last real observation - the same rule as CPI below, so a USD-quoted asset can never get
        // "converted" using a guessed future FX rate.
        var fxRaw = await marketProvider.GetMonthlyHistoryAsync("TRY=X", from, to, ct);
        var lastRealFxMonth = fxRaw.Count == 0 ? (DateOnly?)null : fxRaw.Max(o => o.Date);
        var fxMonths = lastRealFxMonth is { } lastFx ? months.Where(m => m <= lastFx).ToList() : [];
        var fx = ForwardFill(fxRaw, fxMonths);
        if (fx.Count == 0)
            return IngestionReport.Empty with { Notes = ["Aborted: no USD/TRY data from provider."] };

        var priceSource = $"{marketProvider.Name}";
        var existingPriceRows = await db.DailyPrices.ToDictionaryAsync(p => (p.Symbol, p.Date), ct);

        int inserted = 0, updated = 0, skipped = 0, priceRemoved = 0, assets = 0;

        foreach (var spec in AssetCatalog.All)
        {
            ct.ThrowIfCancellationRequested();
            var raw = spec.ProviderSymbol == "TRY=X"
                ? fxRaw
                : await marketProvider.GetMonthlyHistoryAsync(spec.ProviderSymbol, from, to, ct);

            // Each asset keeps its own real-data cutoff - GOLD/BIST100/SP500 and BTC/USDTRY don't
            // necessarily stop at the same month, so forcing one global cutoff would either truncate
            // an asset that actually has fresher data, or forward-fill one that doesn't.
            var lastReal = raw.Count == 0 ? (DateOnly?)null : raw.Max(o => o.Date);
            var assetMonths = lastReal is { } lr ? months.Where(m => m <= lr).ToList() : [];
            var filled = ForwardFill(raw, assetMonths);
            if (filled.Count == 0) { notes.Add($"{spec.Symbol}: no data, skipped."); continue; }
            assets++;

            foreach (var (month, providerClose) in filled)
            {
                if (!fx.TryGetValue(month, out var usdTry)) continue;

                var tryClose = decimal.Round(AssetCatalog.ToTry(spec.Quote, providerClose, usdTry), 6);
                if (tryClose <= 0) continue;

                var key = (spec.Symbol, month);
                if (existingPriceRows.Remove(key, out var existing))
                {
                    if (existing.Close == tryClose) { skipped++; continue; }
                    existing.Update(tryClose, $"{priceSource}:{spec.ProviderSymbol}"); // forward-filled guess turned real, or a provider revision
                    updated++;
                    continue;
                }
                db.DailyPrices.Add(new DailyPrice(spec.Symbol, month, tryClose, $"{priceSource}:{spec.ProviderSymbol}"));
                inserted++;
            }

            // Any row for this asset beyond its own last real month is a stale forward-filled guess
            // from a previous run (before this month's real data existed) - remove it. Only do this
            // when the provider actually returned real data this run, so a transient provider failure
            // can never wipe out a good asset's history.
            if (lastReal is { } realCutoff)
                foreach (var (key, row) in existingPriceRows.Where(kv => kv.Key.Symbol == spec.Symbol && kv.Key.Date > realCutoff).ToList())
                {
                    db.DailyPrices.Remove(row);
                    existingPriceRows.Remove(key);
                    priceRemoved++;
                }
        }
        if (priceRemoved > 0)
            notes.Add($"Prices: removed {priceRemoved} stale forward-filled row(s) beyond each asset's last real observation.");

        // 2. Inflation. Forward-fill only up to FRED's own last real observation - a month with no
        // published CPI yet must stay absent, never get a guessed value that looks real.
        var cpiRaw = await inflationProvider.GetMonthlyIndexAsync(from, to, ct);
        var lastRealCpiMonth = cpiRaw.Count == 0 ? (DateOnly?)null : cpiRaw.Max(o => o.Date);
        var cpiMonths = lastRealCpiMonth is { } lastCpi ? months.Where(m => m <= lastCpi).ToList() : [];
        var cpi = ForwardFill(cpiRaw, cpiMonths);

        var existingCpiRows = await db.InflationIndices.ToDictionaryAsync(x => x.Month, ct);
        int cpiInserted = 0, cpiUpdated = 0, cpiSkipped = 0, cpiRemoved = 0;
        foreach (var (month, index) in cpi)
        {
            var rounded = decimal.Round(index, 6);
            if (existingCpiRows.Remove(month, out var existing))
            {
                if (existing.Index == rounded) { cpiSkipped++; continue; }
                existing.Update(rounded, inflationProvider.Name); // a forward-filled guess turned real, or a provider revision
                cpiUpdated++;
                continue;
            }
            db.InflationIndices.Add(new InflationIndex(month, rounded, inflationProvider.Name));
            cpiInserted++;
        }

        // Anything left over that now falls beyond the last real observation is a stale
        // forward-filled month from a previous run (before this month's real data existed) - remove it
        // rather than leave a fabricated value sitting in the table.
        foreach (var stale in existingCpiRows.Values.Where(x => lastRealCpiMonth is null || x.Month > lastRealCpiMonth))
        {
            db.InflationIndices.Remove(stale);
            cpiRemoved++;
        }
        if (cpiRemoved > 0)
            notes.Add($"CPI: removed {cpiRemoved} stale forward-filled month(s) beyond the last real FRED observation.");

        await db.SaveChangesAsync(ct);

        var report = new IngestionReport(assets, inserted, skipped, cpiInserted, cpiSkipped, notes);
        logger.LogInformation(
            "Ingestion done: {Assets} assets, +{Inserted} prices ({Updated} updated, {PriceRemoved} stale removed, {Skipped} unchanged), " +
            "+{Cpi} CPI ({CpiUpdated} updated, {CpiRemoved} stale removed, {CpiSkipped} unchanged)",
            assets, inserted, updated, priceRemoved, skipped, cpiInserted, cpiUpdated, cpiRemoved, cpiSkipped);
        return report;
    }

    private static IEnumerable<DateOnly> MonthRange(DateOnly from, DateOnly to)
    {
        for (var m = from; m <= to; m = m.AddMonths(1)) yield return m;
    }

    /// <summary>
    /// Carries the last known value forward over missing months. Leading months before the first
    /// observation are dropped (we never back-fill an invented value). This is the only transformation
    /// applied to gap-filling and is documented in the README.
    /// </summary>
    private static Dictionary<DateOnly, decimal> ForwardFill(
        IReadOnlyList<ProviderObservation> observations, IReadOnlyList<DateOnly> months)
    {
        var byMonth = observations
            .GroupBy(o => o.Date).ToDictionary(g => g.Key, g => g.First().Close);
        var result = new Dictionary<DateOnly, decimal>();
        decimal? last = null;
        foreach (var month in months)
        {
            if (byMonth.TryGetValue(month, out var value)) last = value;
            if (last is { } v) result[month] = v;
        }
        return result;
    }
}
