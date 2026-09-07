using Timeback.Domain.ValueObjects;

namespace Timeback.Domain.Scoring;

/// <summary>
/// The full, explainable result of one round. Every number here is computed server-side from
/// historical data - none of it is trusted from the client.
/// </summary>
public sealed record RoundOutcome(
    Money StartingCapital,
    Money FinalValue,
    decimal NominalReturnFraction,
    decimal RealReturnFraction,
    decimal InflationFraction,
    Money BestPossibleValue,
    string BestPossibleSymbol,
    Money WorstPossibleValue,
    Money MissedGain,
    int Score,
    IReadOnlyList<AssetOutcome> AssetOutcomes);

/// <summary>
/// Scoring model (all knobs are named constants, no magic numbers):
///
///   nominal return  = final / start - 1
///   real return     = (1 + nominal) * cpiThen / cpiNow - 1
///   opportunity set = [worst single-asset outcome .. best single-asset outcome] for this round
///   skill           = clamp01( (playerFinal - worst) / (best - worst) )
///   round score     = round( <see cref="MaxRoundScore"/> * skill )
///
/// The score therefore measures allocation skill relative to what was actually achievable that round,
/// not raw luck of the draw - a great pick in a flat market still scores well, a lazy split in a
/// once-in-a-decade rally scores poorly.
/// </summary>
public static class RoundScoring
{
    public const int MaxRoundScore = 1000;
    public const int TotalRounds = 3;
    public const int MaxGameScore = MaxRoundScore * TotalRounds;

    public static RoundOutcome Score(
        PortfolioValuation valuation,
        IReadOnlyDictionary<string, AssetQuote> quotes,
        decimal cpiThen,
        decimal cpiNow)
    {
        var capital = valuation.Initial;
        var nominal = valuation.NominalReturnFraction;
        var inflation = cpiThen == 0 ? 0m : (cpiNow / cpiThen) - 1m;
        var real = ((1m + nominal) * SafeRatio(cpiThen, cpiNow)) - 1m;

        var best = quotes.Values.MaxBy(q => q.GrowthFactor);
        var worst = quotes.Values.MinBy(q => q.GrowthFactor);
        var bestValue = capital.Scale(best.GrowthFactor);
        var worstValue = capital.Scale(worst.GrowthFactor);

        var skill = SkillRatio(valuation.Final.Amount, worstValue.Amount, bestValue.Amount);
        var score = (int)Math.Round(MaxRoundScore * skill, MidpointRounding.AwayFromZero);

        var missed = Money.Of(Math.Max(0m, bestValue.Amount - valuation.Final.Amount), capital.Currency);

        return new RoundOutcome(
            capital, valuation.Final, nominal, real, inflation,
            bestValue, best.Symbol, worstValue, missed, score, valuation.Lines);
    }

    private static decimal SafeRatio(decimal thenValue, decimal nowValue)
        => nowValue == 0 ? 1m : thenValue / nowValue;

    private static decimal SkillRatio(decimal player, decimal worst, decimal best)
    {
        if (best <= worst) return 1m; // no allocation could have done better than any other
        var ratio = (player - worst) / (best - worst);
        return Math.Clamp(ratio, 0m, 1m);
    }
}
