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
    IReadOnlyList<AssetOutcome> AssetOutcomes,
    int InvestmentScore = 0,
    int TimeBonus = 0);

/// <summary>
/// Scoring model (all knobs are named constants, no magic numbers):
///
///   nominal return  = final / start - 1
///   real return     = (1 + nominal) * cpiThen / cpiNow - 1
///   opportunity set = [worst single-asset outcome .. best single-asset outcome] for this round
///   skill           = clamp01( (playerFinal - worst) / (best - worst) )
///   investment mark = round( <see cref="LegacyMaxScore"/> * skill )          - what <see cref="Score"/> below computes
///
/// The investment mark therefore measures allocation skill relative to what was actually achievable
/// that round, not raw luck of the draw - a great pick in a flat market still scores well, a lazy
/// split in a once-in-a-decade rally scores poorly.
///
/// The mark above is only the investment half of a round's score. <see cref="Round"/> combines it
/// with a speed bonus once the real submission time is known:
///
///   investmentScore = floor( <see cref="MaxInvestmentScore"/> * skill )      - via <see cref="ScaleInvestmentScore"/>
///   timeBonus       = clamp( floor(remainingSeconds * <see cref="MaxTimeBonus"/> / 20), 0, <see cref="MaxTimeBonus"/> )
///   round score     = investmentScore + timeBonus
///
/// so a great pick decided instantly still outscores the same pick made with one second left on the
/// clock, while a maxed-out timer never buys back more than the speed bonus's own share of the 1000.
/// </summary>
public static class RoundScoring
{
    /// <summary>The scale <see cref="Score(Games.PortfolioValuation,IReadOnlyDictionary{string,AssetQuote},decimal,decimal)"/>
    /// computes the raw investment mark on, before it is rescaled down to <see cref="MaxInvestmentScore"/>.</summary>
    private const int LegacyMaxScore = 1000;

    public const int MaxInvestmentScore = 700;
    public const int MaxTimeBonus = 300;
    public const int MaxRoundScore = MaxInvestmentScore + MaxTimeBonus; // 1000
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
        var score = (int)Math.Round(LegacyMaxScore * skill, MidpointRounding.AwayFromZero);

        var missed = Money.Of(Math.Max(0m, bestValue.Amount - valuation.Final.Amount), capital.Currency);

        return new RoundOutcome(
            capital, valuation.Final, nominal, real, inflation,
            bestValue, best.Symbol, worstValue, missed, score, valuation.Lines);
    }

    /// <summary>Rescales the raw 0-<see cref="LegacyMaxScore"/> investment mark down to 0-<see cref="MaxInvestmentScore"/>,
    /// a simple, deterministic, order-preserving transform: a better investment mark always yields a
    /// better (or equal) investment score.</summary>
    public static int ScaleInvestmentScore(int legacyScore)
        => (int)Math.Floor(legacyScore * (MaxInvestmentScore / (decimal)LegacyMaxScore));

    /// <summary>Speed bonus for locking in before the deadline, computed from the server's own clock -
    /// never the client's countdown. <paramref name="submittedAtUtc"/> is when the round was actually
    /// resolved (a real submission, or the deadline itself for a timeout); remaining time is measured
    /// against <paramref name="windowEndUtc"/>, which must be the real 20-second selection window's end
    /// (<see cref="Round.StartedAtUtc"/> + 20s) - NOT <see cref="Round.EndsAtUtc"/>, which additionally
    /// carries the round's network-grace allowance. A submission that only lands inside that grace
    /// window (still accepted, per the round's normal deadline rules) earns no time bonus: the grace
    /// exists purely as network tolerance, never as extra scoring time.</summary>
    public static int TimeBonus(DateTime windowEndUtc, DateTime submittedAtUtc)
    {
        var remainingSeconds = (decimal)(windowEndUtc - submittedAtUtc).TotalSeconds;
        // 20 = the selection window in seconds (must track Game.SelectionWindow): a full window
        // remaining scores the whole MaxTimeBonus, zero remaining scores nothing, linear in between.
        var bonus = (int)Math.Floor(Math.Max(0m, remainingSeconds) * MaxTimeBonus / 20m);
        return Math.Clamp(bonus, 0, MaxTimeBonus);
    }

    /// <summary>Combines an investment-only <see cref="RoundOutcome"/> with the speed bonus for when it
    /// was actually submitted, producing the final round score (investment + time, capped at <see cref="MaxRoundScore"/>).
    /// <paramref name="windowEndUtc"/> must be the real 20-second selection window's end (see <see cref="TimeBonus"/>).</summary>
    public static RoundOutcome ApplyTimeBonus(RoundOutcome outcome, DateTime windowEndUtc, DateTime submittedAtUtc)
    {
        var investmentScore = ScaleInvestmentScore(outcome.Score);
        var timeBonus = TimeBonus(windowEndUtc, submittedAtUtc);
        return outcome with { InvestmentScore = investmentScore, TimeBonus = timeBonus, Score = investmentScore + timeBonus };
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
