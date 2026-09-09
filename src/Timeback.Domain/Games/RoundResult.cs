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
    /// <summary>Investment-performance component of <see cref="Score"/>, 0-<see cref="Scoring.RoundScoring.MaxInvestmentScore"/>.
    /// Null only for a round completed before this breakdown existed: its persisted JSON simply has no
    /// value for this property, so deserialization leaves it unset rather than a misleading 0 - that
    /// old game's history is never rewritten, its <see cref="Score"/> total remains exactly as scored.</summary>
    public int? InvestmentScore { get; private set; }
    /// <summary>Speed-bonus component of <see cref="Score"/>, 0-<see cref="Scoring.RoundScoring.MaxTimeBonus"/>. Null under
    /// the same pre-existing-record condition as <see cref="InvestmentScore"/>.</summary>
    public int? TimeBonus { get; private set; }

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
            Score = o.Score,
            InvestmentScore = o.InvestmentScore,
            TimeBonus = o.TimeBonus
        };
        foreach (var a in o.AssetOutcomes)
            r._assets.Add(new RoundAssetResult(a.Symbol, a.GrowthFactor));
        return r;
    }
}

/// <summary>Per-asset outcome as persisted/exposed - just enough to render "this asset moved X%".
/// The domain's own scoring math (<see cref="Scoring.AssetOutcome"/>) still carries the full
/// invested/final Money values internally; only the two fields nothing downstream ever read
/// (invested amount, final value) were dropped from what gets stored here.</summary>
public sealed class RoundAssetResult
{
    public string Symbol { get; private set; } = null!;
    public decimal GrowthFactor { get; private set; }
    private RoundAssetResult() { }
    public RoundAssetResult(string symbol, decimal growthFactor)
    {
        Symbol = symbol; GrowthFactor = growthFactor;
    }
}
