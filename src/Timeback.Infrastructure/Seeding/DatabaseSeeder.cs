using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Timeback.Domain.Assets;
using Timeback.Infrastructure.MarketData;
using Timeback.Infrastructure.MarketData.Ingestion;
using Timeback.Infrastructure.Persistence;

namespace Timeback.Infrastructure.Seeding;

/// <summary>
/// Idempotent boot sequence: apply migrations, ensure the asset catalog exists, then run market-data
/// ingestion (which is itself idempotent) so a fresh database ends up fully playable.
/// </summary>
public sealed class DatabaseSeeder(
    TimebackDbContext db,
    MarketDataIngestionService ingestion,
    ILogger<DatabaseSeeder> logger)
{
    private static readonly DateOnly IngestFrom = new(2015, 1, 1);

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await ReconcileAssetsAsync(ct);

        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var report = await ingestion.IngestAsync(IngestFrom, to, ct);
        logger.LogInformation(
            "Seed ingestion: +{Prices} prices, +{Cpi} CPI ({SkipP} / {SkipC} already present)",
            report.PricesInserted, report.InflationInserted,
            report.PricesSkippedExisting, report.InflationSkippedExisting);
    }

    /// <summary>Keeps the assets table in sync with <see cref="AssetCatalog"/>: inserts new entries,
    /// re-syncs the display name/class of existing ones (e.g. a labelling fix), and deactivates any
    /// asset no longer in the catalog (e.g. removed from play) without deleting its historical prices.</summary>
    private async Task ReconcileAssetsAsync(CancellationToken ct)
    {
        var existing = await db.Assets.ToDictionaryAsync(a => a.Symbol, ct);
        var catalogSymbols = AssetCatalog.All.Select(s => s.Symbol).ToHashSet();

        int inserted = 0, updated = 0, deactivated = 0;
        foreach (var spec in AssetCatalog.All)
        {
            if (existing.Remove(spec.Symbol, out var asset))
            {
                if (asset.DisplayName != spec.DisplayName || asset.Class != spec.Class || !asset.IsActive)
                {
                    asset.SyncFromCatalog(spec.DisplayName, spec.Class);
                    updated++;
                }
                continue;
            }
            db.Assets.Add(new Asset(spec.Symbol, spec.DisplayName, spec.Class));
            inserted++;
        }

        foreach (var asset in existing.Values.Where(a => a.IsActive && !catalogSymbols.Contains(a.Symbol)))
        {
            asset.Deactivate();
            deactivated++;
        }

        if (inserted > 0 || updated > 0 || deactivated > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Assets reconciled: +{Inserted} new, {Updated} updated, {Deactivated} deactivated",
                inserted, updated, deactivated);
        }
    }
}
