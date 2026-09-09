using FluentAssertions;
using Timeback.Application.Abstractions;
using Timeback.Application.Games;
using Timeback.Domain.Assets;
using Timeback.Domain.Scoring;

namespace Timeback.Application.Tests;

public class RoundDatePlannerTests
{
    /// <summary>
    /// Synthetic 10-year monthly data with deliberate regimes: BTC explodes early then stalls,
    /// gold ramps mid, equities ramp late, USD/TRY grinds up throughout.
    /// </summary>
    private sealed class RegimeStore : IMarketDataStore
    {
        private static readonly DateOnly Start = new(2015, 1, 1);
        private static readonly Dictionary<string, IReadOnlyList<PricePoint>> S = Build();

        private static Dictionary<string, IReadOnlyList<PricePoint>> Build()
        {
            List<PricePoint> Gen(Func<int, decimal> f) =>
                Enumerable.Range(0, 132).Select(m => new PricePoint(Start.AddMonths(m), Math.Max(1m, f(m)))).ToList();

            return new()
            {
                ["BTC"] = Gen(m => m < 36 ? 100m + m * m * 2m : 100m + 36m * 36m * 2m + (m - 36) * 5m),
                ["GOLD"] = Gen(m => m < 48 ? 100m + m : 100m + 48m + (m - 48) * 12m),
                ["SP500"] = Gen(m => m < 84 ? 100m + m * 0.5m : 100m + 42m + (m - 84) * 20m),
                ["USDTRY"] = Gen(m => 100m + m * 3m),
            };
        }

        public Task<IReadOnlyList<Asset>> GetActiveAssetsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Asset>>(S.Keys.Select(k =>
                new Asset(k, k, AssetClass.Crypto)).ToList());

        public Task<IReadOnlyDictionary<string, IReadOnlyList<PricePoint>>> GetPriceSeriesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<PricePoint>>>(S);

        public Task<(DateOnly Earliest, DateOnly Latest)> GetCoverageAsync(CancellationToken ct)
            => Task.FromResult((S["BTC"][0].Date, S["BTC"][^1].Date));

        public Task<DateOnly?> ResolveEffectiveMarketDateAsync(DateOnly requested, CancellationToken ct)
            => Task.FromResult<DateOnly?>(new DateOnly(requested.Year, requested.Month, 1));

        public Task<IReadOnlyDictionary<string, AssetQuote>> GetQuotesAsync(DateOnly e, DateOnly v, CancellationToken ct)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task Produces_five_rounds_with_meaningful_winner_variety()
    {
        var planner = new RoundDatePlanner(new RegimeStore());
        var rounds = await planner.PlanAsync(seed: 12345, default);

        rounds.Should().HaveCount(RoundScoring.TotalRounds);
        rounds.Select(r => r.EffectiveMarketDate).Should().OnlyHaveUniqueItems();

        // Which asset actually won each round's window?
        var series = await new RegimeStore().GetPriceSeriesAsync(default);
        decimal At(string s, DateOnly d) { decimal last = series[s][0].Close; foreach (var p in series[s]) { if (p.Date > d) break; last = p.Close; } return last; }

        var winners = rounds.Select(r =>
            series.Keys.MaxBy(s => At(s, r.ValuationDate) / At(s, r.EffectiveMarketDate))!).ToList();

        winners.Distinct().Count().Should().BeGreaterThanOrEqualTo(3,
            "the planner should spread rounds across market regimes, not let one asset dominate");
    }

    [Fact]
    public async Task Is_deterministic_for_a_given_seed()
    {
        var a = await new RoundDatePlanner(new RegimeStore()).PlanAsync(999, default);
        var b = await new RoundDatePlanner(new RegimeStore()).PlanAsync(999, default);
        a.Select(x => (x.EffectiveMarketDate, x.ValuationDate))
            .Should().Equal(b.Select(x => (x.EffectiveMarketDate, x.ValuationDate)));
    }
}
