using Timeback.Domain.Assets;
using Timeback.Domain.Scoring;

namespace Timeback.Application.Abstractions;

/// <summary>
/// Read model over the immutable historical dataset in PostgreSQL. This is the ONLY source of prices
/// during play - no external API is called mid-game. Implementations cache aggressively because the
/// underlying data never changes during a run.
/// </summary>
public interface IMarketDataStore
{
    Task<IReadOnlyList<Asset>> GetActiveAssetsAsync(CancellationToken ct);

    /// <summary>The earliest and latest dates for which every active asset has a price.</summary>
    Task<(DateOnly Earliest, DateOnly Latest)> GetCoverageAsync(CancellationToken ct);

    /// <summary>Nearest trading day at or before <paramref name="requested"/> that has prices for all active assets.</summary>
    Task<DateOnly?> ResolveEffectiveMarketDateAsync(DateOnly requested, CancellationToken ct);

    /// <summary>Entry price (at <paramref name="effectiveDate"/>) and valuation price (at <paramref name="valuationDate"/>) per active asset.</summary>
    Task<IReadOnlyDictionary<string, AssetQuote>> GetQuotesAsync(
        DateOnly effectiveDate, DateOnly valuationDate, CancellationToken ct);

    /// <summary>Full ascending close series per active asset symbol. Used by round planning.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<PricePoint>>> GetPriceSeriesAsync(CancellationToken ct);
}

public readonly record struct PricePoint(DateOnly Date, decimal Close);
