namespace Timeback.Application.Abstractions;

/// <summary>One raw observation from an external provider, in the provider's own quote currency.</summary>
public readonly record struct ProviderObservation(DateOnly Date, decimal Close);

/// <summary>
/// A replaceable external historical-price source. Provider-specific details (HTTP, CSV parsing,
/// symbol names, auth) live entirely behind this interface, inside Infrastructure. The domain and
/// game engine never see it - they only read the ingested <c>daily_prices</c> table.
/// </summary>
public interface IMarketDataProvider
{
    /// <summary>Stable identifier for logging/traceability, e.g. "yahoo-finance", "embedded-csv".</summary>
    string Name { get; }

    /// <summary>
    /// Monthly close history for one provider symbol (e.g. "GC=F", "BTC-USD") within the range,
    /// ascending by date. May contain gaps; the ingestion layer normalises them.
    /// </summary>
    Task<IReadOnlyList<ProviderObservation>> GetMonthlyHistoryAsync(
        string providerSymbol, DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>A replaceable external inflation-index source (CPI).</summary>
public interface IInflationDataProvider
{
    string Name { get; }
    Task<IReadOnlyList<ProviderObservation>> GetMonthlyIndexAsync(DateOnly from, DateOnly to, CancellationToken ct);
}
