using Timeback.Domain.Scoring;

namespace Timeback.Domain.Games;

/// <summary>Persisted, flattened snapshot of a round's scored outcome (owned by <see cref="Round"/>).</summary>
public sealed class RoundResult
{
    public decimal StartingCapital { get; private set; }
    public decimal FinalValue { get; private set; }
    public decimal NominalReturnFraction { get; private set; }
    public decimal RealReturnFraction { get; private set; }
    public decimal InflationFraction { get; private set; }
    public decimal BestPossibleValue { get; private set; }
    public string BestPossibleSymbol { get; private set; } = null!;
    public decimal WorstPossibleValue { get; private set; }
    public decimal MissedGain { get; private set; }
    public int Score { get; private set; }

    private readonly List<RoundAssetResult> _assets = [];
    public IReadOnlyList<RoundAssetResult> Assets => _assets;

    private RoundResult() { }

    public static RoundResult From(RoundOutcome o)
    {
        var r = new RoundResult
        {
            StartingCapital = o.StartingCapital.Amount,
            FinalValue = o.FinalValue.Amount,
            NominalReturnFraction = o.NominalReturnFraction,
            RealReturnFraction = o.RealReturnFraction,
            InflationFraction = o.InflationFraction,
            BestPossibleValue = o.BestPossibleValue.Amount,
            BestPossibleSymbol = o.BestPossibleSymbol,
            WorstPossibleValue = o.WorstPossibleValue.Amount,
            MissedGain = o.MissedGain.Amount,
            Score = o.Score
        };
        foreach (var a in o.AssetOutcomes)
            r._assets.Add(new RoundAssetResult(a.Symbol, a.Invested.Amount, a.FinalValue.Amount, a.GrowthFactor));
        return r;
    }
}

public sealed class RoundAssetResult
{
    public string Symbol { get; private set; } = null!;
    public decimal Invested { get; private set; }
    public decimal FinalValue { get; private set; }
    public decimal GrowthFactor { get; private set; }
    private RoundAssetResult() { }
    public RoundAssetResult(string symbol, decimal invested, decimal finalValue, decimal growthFactor)
    {
        Symbol = symbol; Invested = invested; FinalValue = finalValue; GrowthFactor = growthFactor;
    }
}
