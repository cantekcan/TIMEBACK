using Timeback.Domain.Common;

namespace Timeback.Domain.MarketData;

/// <summary>
/// The close price of one asset on one calendar date, expressed in TRY (the game's single currency).
/// Historical data only - this table is the game's source of truth and is never read from a live API
/// during play. Prices are <see cref="decimal"/> to keep valuation math exact. <see cref="Source"/>
/// records where the row came from (e.g. "yahoo:GC=F", "seed:embedded-csv") for full traceability.
/// </summary>
public sealed class DailyPrice : Entity
{
    public string Symbol { get; private set; } = null!;
    public DateOnly Date { get; private set; }
    public decimal Close { get; private set; }
    public string Source { get; private set; } = "unknown";

    private DailyPrice() { } // EF

    public DailyPrice(string symbol, DateOnly date, decimal close, string source)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new DomainException("Price symbol is required.");
        if (close <= 0) throw new DomainException($"Price close must be positive (got {close} for {symbol} {date}).");
        Symbol = symbol.ToUpperInvariant();
        Date = date;
        Close = close;
        Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
    }

    /// <summary>Corrects this date's close - used when a re-ingestion finds a fresher value
    /// (a real observation replacing an earlier forward-filled guess, or a provider revision).</summary>
    public void Update(decimal close, string source)
    {
        if (close <= 0) throw new DomainException($"Price close must be positive (got {close} for {Symbol} {Date}).");
        Close = close;
        Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
    }
}
