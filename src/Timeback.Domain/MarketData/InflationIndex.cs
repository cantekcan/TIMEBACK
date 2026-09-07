using Timeback.Domain.Common;

namespace Timeback.Domain.MarketData;

/// <summary>
/// A monthly consumer price index level. Real returns are derived purely from the ratio of two index
/// levels - the game never invents an inflation rate. The base period is arbitrary (it cancels in the
/// ratio); the bundled series is OECD "CPI all items, Turkey" (2015 = 100).
/// </summary>
public sealed class InflationIndex : Entity
{
    /// <summary>First day of the index month (UTC, day = 1).</summary>
    public DateOnly Month { get; private set; }
    public decimal Index { get; private set; }
    public string Source { get; private set; } = "unknown";

    private InflationIndex() { } // EF

    public InflationIndex(DateOnly month, decimal index, string source)
    {
        if (index <= 0) throw new DomainException($"Inflation index must be positive (got {index}).");
        Month = new DateOnly(month.Year, month.Month, 1);
        Index = index;
        Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
    }

    /// <summary>Corrects this month's level - used when a re-ingestion finds a fresher value
    /// (a real observation replacing an earlier forward-filled guess, or a provider revision).</summary>
    public void Update(decimal index, string source)
    {
        if (index <= 0) throw new DomainException($"Inflation index must be positive (got {index}).");
        Index = index;
        Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
    }
}
