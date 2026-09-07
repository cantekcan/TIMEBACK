using Timeback.Application.Abstractions;
using Timeback.Domain.Common;
using Timeback.Domain.Games;
using Timeback.Domain.Scoring;

namespace Timeback.Application.Games;

/// <summary>
/// Picks the five (entry, valuation) date pairs for a game.
///
/// Goal: meaningful, replayable variety without touching a single historical price.
///
///  * Each round holds for a short, varied horizon (1-4 years). Long "check back today" windows let
///    BTC + the lira slide turn every round into "pick the moonshot"; short windows let different
///    assets genuinely win different periods (BTC 2018→2019 is down; gold 2018→2020 is up; etc.).
///  * Windows whose best/worst asset ratio is absurd (> <see cref="MaxSpread"/>x) or whose winner
///    more than <see cref="MaxBestFactor"/>x'd are filtered out - they are "guess the winner or score
///    zero", not strategy. The underlying returns are still 100% real; the game just doesn't *show*
///    you the degenerate windows.
///  * The five chosen windows maximise the number of distinct winning assets, are spread apart in
///    time, and are sampled (seeded) so replays differ.
/// </summary>
public sealed class RoundDatePlanner(IMarketDataStore marketData)
{
    private static readonly int[] HorizonYears = [1, 2, 3, 4];
    private const int MinSeparationMonths = 8;
    private const decimal MaxSpread = 12m;
    private const decimal MaxBestFactor = 8m;
    private const decimal MinSpread = 1.4m;

    private readonly record struct Candidate(
        DateOnly Entry, DateOnly Valuation, string Winner, decimal BestFactor, decimal Spread);

    public async Task<IReadOnlyList<RoundDates>> PlanAsync(int seed, CancellationToken ct)
    {
        var series = await marketData.GetPriceSeriesAsync(ct);
        if (series.Count == 0)
            throw new DomainException("Historical dataset is empty - run market-data ingestion.");

        var (earliest, latest) = await marketData.GetCoverageAsync(ct);
        var all = BuildCandidates(series, earliest, latest);
        if (all.Count < RoundScoring.TotalRounds)
            throw new DomainException("Historical dataset is too short to plan a game.");

        var rng = new Random(seed);
        var chosen = SelectDiverse(Playable(all), all, rng);

        var result = new List<RoundDates>(RoundScoring.TotalRounds);
        foreach (var c in chosen.OrderBy(c => c.Entry))
        {
            // The dataset is monthly, so the earliest usable price for this window is c.Entry itself
            // (resolved defensively in case that exact month is ever missing for some asset). The date
            // shown to the player must be this SAME date - a cosmetic "random day in the month" offset
            // here would silently disagree with the price actually used to value the round.
            var effective = await marketData.ResolveEffectiveMarketDateAsync(c.Entry, ct) ?? c.Entry;
            result.Add(new RoundDates(effective, effective, c.Valuation));
        }
        return result;
    }

    private static List<Candidate> BuildCandidates(
        IReadOnlyDictionary<string, IReadOnlyList<PricePoint>> series, DateOnly earliest, DateOnly latest)
    {
        var lookup = series.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToDictionary(p => new DateOnly(p.Date.Year, p.Date.Month, 1), p => p.Close));

        var candidates = new List<Candidate>();
        for (var entry = new DateOnly(earliest.Year, earliest.Month, 1); entry <= latest; entry = entry.AddMonths(1))
        {
            foreach (var h in HorizonYears)
            {
                var valuation = entry.AddYears(h);
                if (valuation > latest) continue;

                var factors = new List<(string Symbol, decimal Factor)>();
                foreach (var (symbol, byMonth) in lookup)
                    if (byMonth.TryGetValue(entry, out var p0) && byMonth.TryGetValue(valuation, out var p1)
                        && p0 > 0 && p1 > 0)
                        factors.Add((symbol, p1 / p0));
                if (factors.Count < 2) continue;

                var best = factors.MaxBy(f => f.Factor);
                var worst = factors.MinBy(f => f.Factor);
                candidates.Add(new Candidate(entry, valuation, best.Symbol, best.Factor, best.Factor / worst.Factor));
            }
        }
        return candidates;
    }

    /// <summary>Windows that make a decision meaningful without being a pure lottery. Relaxes the caps if too strict.</summary>
    private static List<Candidate> Playable(List<Candidate> all)
    {
        for (var relax = 0m; relax <= 40m; relax += 8m)
        {
            var pool = all.Where(c =>
                c.Spread is >= MinSpread &&
                c.Spread <= MaxSpread + relax &&
                c.BestFactor <= MaxBestFactor + relax).ToList();
            if (pool.Select(c => c.Winner).Distinct().Count() >= Math.Min(4, RoundScoring.TotalRounds)
                && pool.Count >= RoundScoring.TotalRounds * 3)
                return pool;
        }
        return all;
    }

    private static List<Candidate> SelectDiverse(List<Candidate> playable, List<Candidate> all, Random rng)
    {
        var chosen = new List<Candidate>();

        // Pass 1: one window per distinct winning asset, asset order and window both seeded.
        foreach (var group in playable.GroupBy(c => c.Winner).OrderBy(_ => rng.Next()))
        {
            if (chosen.Count == RoundScoring.TotalRounds) break;
            var pick = group.OrderBy(_ => rng.Next()).FirstOrDefault(c => FarEnough(c, chosen));
            if (pick != default) chosen.Add(pick);
        }

        // Pass 2: remaining slots -> least-represented winner first, seeded.
        while (chosen.Count < RoundScoring.TotalRounds)
        {
            var counts = chosen.GroupBy(c => c.Winner).ToDictionary(g => g.Key, g => g.Count());
            var eligible = playable.Where(c => FarEnough(c, chosen) && !chosen.Contains(c)).ToList();
            if (eligible.Count == 0) break;
            var pick = eligible
                .OrderBy(c => counts.GetValueOrDefault(c.Winner))
                .ThenBy(_ => rng.Next())
                .First();
            chosen.Add(pick);
        }

        // Pass 3 (pathologically small datasets): ignore separation.
        foreach (var c in all.OrderBy(_ => rng.Next()))
        {
            if (chosen.Count == RoundScoring.TotalRounds) break;
            if (!chosen.Contains(c)) chosen.Add(c);
        }

        return chosen;
    }

    private static bool FarEnough(Candidate c, IEnumerable<Candidate> chosen)
        => chosen.All(x => Math.Abs(((c.Entry.Year - x.Entry.Year) * 12) + c.Entry.Month - x.Entry.Month) >= MinSeparationMonths);
}
