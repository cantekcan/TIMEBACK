using Timeback.Domain.Common;
using Timeback.Domain.ValueObjects;

namespace Timeback.Domain.Scoring;

/// <summary>A single asset's price at the round's entry date and at the valuation ("today") date.</summary>
public readonly record struct AssetQuote(string Symbol, decimal PriceThen, decimal PriceNow)
{
    /// <summary>Multiplicative growth over the holding period, e.g. 3.0 = tripled.</summary>
    public decimal GrowthFactor => PriceNow / PriceThen;
}

/// <summary>One line of the player's requested allocation.</summary>
public readonly record struct AllocationLine(string Symbol, Percentage Weight);

/// <summary>Per-asset outcome of holding the allocated slice from entry to valuation date.</summary>
public readonly record struct AssetOutcome(
    string Symbol,
    Money Invested,
    Money FinalValue,
    decimal GrowthFactor)
{
    /// <summary>Return on this slice as a fraction, e.g. 0.5 = +50%.</summary>
    public decimal ReturnFraction => GrowthFactor - 1m;
}

public sealed record PortfolioValuation(
    Money Initial,
    Money Final,
    IReadOnlyList<AssetOutcome> Lines)
{
    /// <summary>Nominal return as a fraction of starting capital.</summary>
    public decimal NominalReturnFraction =>
        Initial.Amount == 0 ? 0m : (Final.Amount / Initial.Amount) - 1m;
}

/// <summary>
/// Pure domain service. Turns a capital amount + an allocation + the round's quotes into a valuation.
/// Deterministic and side-effect free so it is trivial to unit test and impossible to game from the client.
/// </summary>
public static class PortfolioCalculator
{
    public static PortfolioValuation Value(
        Money capital,
        IReadOnlyList<AllocationLine> allocations,
        IReadOnlyDictionary<string, AssetQuote> quotes)
    {
        if (allocations.Count == 0)
            throw new DomainException("An allocation must contain at least one line.");

        var lines = new List<AssetOutcome>(allocations.Count);
        var final = Money.Zero(capital.Currency);

        foreach (var line in allocations)
        {
            if (!quotes.TryGetValue(line.Symbol, out var quote))
                throw new DomainException($"No market quote available for asset '{line.Symbol}' this round.");

            var invested = capital.Scale(line.Weight.AsFraction);
            var finalValue = invested.Scale(quote.GrowthFactor);
            final = final.Add(finalValue);
            lines.Add(new AssetOutcome(line.Symbol, invested, finalValue, quote.GrowthFactor));
        }

        return new PortfolioValuation(capital, final, lines);
    }
}
