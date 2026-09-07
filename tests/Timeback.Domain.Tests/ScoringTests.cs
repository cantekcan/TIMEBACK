using FluentAssertions;
using Timeback.Domain.Scoring;
using Timeback.Domain.ValueObjects;

namespace Timeback.Domain.Tests;

public class ScoringTests
{
    private static Dictionary<string, AssetQuote> Quotes(params (string s, decimal then, decimal now)[] q)
        => q.ToDictionary(x => x.s, x => new AssetQuote(x.s, x.then, x.now));

    [Fact]
    public void Portfolio_value_is_capital_weighted_by_growth_factors()
    {
        var quotes = Quotes(("GOLD", 100, 300), ("BTC", 10, 5)); // gold x3, btc x0.5
        var alloc = new List<AllocationLine>
        {
            new("GOLD", Percentage.Of(50)),
            new("BTC", Percentage.Of(50)),
        };

        var v = PortfolioCalculator.Value(Money.Of(100_000), alloc, quotes);

        // 50k * 3 + 50k * 0.5 = 150k + 25k = 175k
        v.Final.Amount.Should().Be(175_000m);
        v.NominalReturnFraction.Should().Be(0.75m);
    }

    [Fact]
    public void Real_return_uses_cpi_ratio_not_an_invented_rate()
    {
        var quotes = Quotes(("GOLD", 100, 200));
        var v = PortfolioCalculator.Value(Money.Of(100_000), [new("GOLD", Percentage.Of(100))], quotes);

        // nominal +100%, inflation: cpi 100 -> 150 => +50%
        var outcome = RoundScoring.Score(v, quotes, cpiThen: 100m, cpiNow: 150m);

        outcome.NominalReturnFraction.Should().Be(1m);
        outcome.InflationFraction.Should().Be(0.5m);
        // (1+1)*100/150 - 1 = 1.3333 - 1
        outcome.RealReturnFraction.Should().BeApproximately(0.33333m, 0.0001m);
    }

    [Fact]
    public void Best_pick_scores_full_marks_worst_pick_scores_zero()
    {
        var quotes = Quotes(("WIN", 100, 400), ("LOSE", 100, 50));

        var best = PortfolioCalculator.Value(Money.Of(100_000), [new("WIN", Percentage.Of(100))], quotes);
        var worst = PortfolioCalculator.Value(Money.Of(100_000), [new("LOSE", Percentage.Of(100))], quotes);

        RoundScoring.Score(best, quotes, 100, 100).Score.Should().Be(RoundScoring.MaxRoundScore);
        RoundScoring.Score(worst, quotes, 100, 100).Score.Should().Be(0);
    }

    [Fact]
    public void Missed_gain_is_the_gap_to_the_best_single_asset_outcome()
    {
        var quotes = Quotes(("WIN", 100, 400), ("LOSE", 100, 100));
        var v = PortfolioCalculator.Value(Money.Of(100_000), [new("WIN", Percentage.Of(50)), new("LOSE", Percentage.Of(50))], quotes);

        var o = RoundScoring.Score(v, quotes, 100, 100);
        o.BestPossibleValue.Amount.Should().Be(400_000m);
        o.FinalValue.Amount.Should().Be(250_000m);
        o.MissedGain.Amount.Should().Be(150_000m);
        o.BestPossibleSymbol.Should().Be("WIN");
    }

    [Fact]
    public void Score_is_independent_of_inflation_only_the_real_return_field_changes()
    {
        var quotes = Quotes(("WIN", 100, 400), ("LOSE", 100, 50));
        var v = PortfolioCalculator.Value(Money.Of(100_000),
            [new("WIN", Percentage.Of(60)), new("LOSE", Percentage.Of(40))], quotes);

        var lowInflation = RoundScoring.Score(v, quotes, cpiThen: 100m, cpiNow: 105m);
        var hyperInflation = RoundScoring.Score(v, quotes, cpiThen: 100m, cpiNow: 900m);

        lowInflation.Score.Should().Be(hyperInflation.Score);          // skill unaffected
        lowInflation.NominalReturnFraction.Should().Be(hyperInflation.NominalReturnFraction);
        hyperInflation.RealReturnFraction.Should().BeLessThan(lowInflation.RealReturnFraction);
    }

    [Fact]
    public void Real_return_is_the_correctly_deflated_nominal_return()
    {
        var quotes = Quotes(("A", 100, 250)); // nominal +150%
        var v = PortfolioCalculator.Value(Money.Of(100_000), [new("A", Percentage.Of(100))], quotes);
        var o = RoundScoring.Score(v, quotes, cpiThen: 200m, cpiNow: 500m); // prices 2.5x

        // real = (1 + 1.5) * 200/500 - 1 = 2.5 * 0.4 - 1 = 0  -> exactly kept pace with inflation
        o.RealReturnFraction.Should().BeApproximately(0m, 0.0001m);
    }

    [Fact]
    public void Flat_market_gives_full_marks_since_no_allocation_could_do_better()
    {
        var quotes = Quotes(("A", 100, 100), ("B", 100, 100));
        var v = PortfolioCalculator.Value(Money.Of(100_000), [new("A", Percentage.Of(100))], quotes);
        RoundScoring.Score(v, quotes, 100, 100).Score.Should().Be(RoundScoring.MaxRoundScore);
    }
}
