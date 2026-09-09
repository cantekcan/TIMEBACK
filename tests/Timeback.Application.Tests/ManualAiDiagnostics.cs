using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Timeback.Application.Abstractions;
using Timeback.Domain.Scoring;
using Timeback.Domain.ValueObjects;
using Timeback.Infrastructure.Ai;
using Xunit.Abstractions;

namespace Timeback.Application.Tests;

/// <summary>
/// Manual/diagnostic only - NOT part of the automated suite. Makes real network calls to Gemini's live
/// API using a real key read from the GEMINI_API_KEY environment variable (never hardcoded, never
/// logged) and prints the actual roasts for human review. Skipped by default so `dotnet test` never
/// depends on network access or burns real API quota.
///
/// To run it: temporarily delete the Skip reason below, then
///   GEMINI_API_KEY=... dotnet test --filter FullyQualifiedName~ManualAiDiagnostics -v n
/// (the `-v n` verbosity is what makes xUnit print ITestOutputHelper output to the console).
/// </summary>
public class ManualAiDiagnostics(ITestOutputHelper output)
{
    private static readonly string[] Assets = ["GOLD", "BIST100", "BTC", "SP500"];

    [Fact(Skip = "Manual diagnostic only - hits the real Gemini API. Delete this Skip to run explicitly.")]
    public async Task Five_real_gemini_roasts_across_varied_game_shapes()
    {
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        apiKey.Should().NotBeNullOrWhiteSpace("set GEMINI_API_KEY to run this diagnostic");

        var commentator = new GeminiAiCommentator(
            new HttpClient { BaseAddress = new Uri("https://generativelanguage.googleapis.com") },
            Options.Create(new GeminiOptions { ApiKey = apiKey!, PrimaryModel = "gemini-3.5-flash-lite", FallbackModel = "gemini-3.1-flash-lite" }),
            new FallbackAiCommentator(),
            new TestOutputLogger<GeminiAiCommentator>(output));

        var scenarios = new (string Name, GameSummaryForAi Summary)[]
        {
            ("Çok kötü / 0 puanlı", BuildWorstEveryRound(seed: 1)),
            ("Dengeli allocation", BuildEqualSplitEveryRound(seed: 2)),
            ("Tek varlığa aşırı yüklenme", BuildAllInOneAsset(seed: 3, asset: "BTC")),
            ("Çok başarılı oyun", BuildBestEveryRound(seed: 4)),
            ("Karışık / ilginç kararlar", BuildRandomMix(seed: 5)),
        };

        var seenComments = new List<string>();

        for (var i = 0; i < scenarios.Length; i++)
        {
            var (name, summary) = scenarios[i];
            var comment = await commentator.CommentAsync(summary, default);
            seenComments.Add(comment.Text);

            output.WriteLine($"=== AI TEST {i + 1}: {name} ===");
            output.WriteLine("Round summary:");
            foreach (var r in summary.Rounds)
            {
                var alloc = string.Join(", ", r.Allocation.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} %{kv.Value}"));
                output.WriteLine($"  Round {r.Number}: {r.DecisionTimeSeconds:0.0}s karar, {alloc}, skor {r.Score}");
            }
            output.WriteLine($"Toplam skor: {summary.FinalScore}/{summary.MaxScore}");
            output.WriteLine("AI:");
            output.WriteLine($"\"{comment.Text}\"");
            output.WriteLine($"[model={comment.Model ?? "FALLBACK"}, uzunluk={comment.Text.Length}]");
            output.WriteLine("");
        }

        output.WriteLine("=== ÇEŞİTLİLİK ===");
        output.WriteLine($"5 yorumdan {seenComments.Distinct().Count()} tanesi birbirinden farklı.");
    }

    private static Dictionary<string, AssetQuote> RandomQuotes(Random rng) =>
        Assets.ToDictionary(a => a, a => new AssetQuote(a, 100m, 100m * (0.3m + (decimal)rng.NextDouble() * 2.7m)));

    private static decimal RandomDecisionTime(Random rng) => Math.Round((decimal)(rng.NextDouble() * 10), 2);

    /// <summary>Scores one round with the real domain math (no shortcuts) and returns everything the AI
    /// prompt needs for it.</summary>
    private static RoundSummaryForAi ScoreRound(int number, Dictionary<string, int> allocationPct, Dictionary<string, AssetQuote> quotes, decimal decisionTime)
    {
        var lines = allocationPct.Where(kv => kv.Value > 0).Select(kv => new AllocationLine(kv.Key, Percentage.Of(kv.Value))).ToList();
        var valuation = PortfolioCalculator.Value(Money.Of(100_000m), lines, quotes);
        var outcome = RoundScoring.Score(valuation, quotes, cpiThen: 100m, cpiNow: 112m);
        return new RoundSummaryForAi(number, outcome.Score, AutoLocked: false, decisionTime, allocationPct);
    }

    private static GameSummaryForAi Assemble(string scenarioTag, List<RoundSummaryForAi> rounds, List<decimal> missedGains)
    {
        var roundScores = rounds.Select(r => r.Score).ToList();
        var best = rounds.MaxBy(r => r.Score)!;
        var worst = rounds.MinBy(r => r.Score)!;
        string Describe(RoundSummaryForAi r) =>
            $"Round {r.Number}: {r.Allocation.OrderByDescending(kv => kv.Value).First().Key} %{r.Allocation.OrderByDescending(kv => kv.Value).First().Value}, {r.Score} puan";

        var avgByAsset = rounds
            .SelectMany(r => r.Allocation)
            .GroupBy(kv => kv.Key)
            .ToDictionary(g => g.Key, g => (int)Math.Round(g.Sum(kv => kv.Value) / (double)rounds.Count))
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new GameSummaryForAi(
            roundScores.Sum(), RoundScoring.MaxGameScore, roundScores,
            Describe(best), Describe(worst), avgByAsset, missedGains.Sum(), rounds);
    }

    /// <summary>Scenario 1: the player always picks that round's worst-performing asset - guarantees
    /// score 0 every round via the real skill-ratio formula (player == worst -> skill 0).</summary>
    private static GameSummaryForAi BuildWorstEveryRound(int seed)
    {
        var rng = new Random(seed);
        var rounds = new List<RoundSummaryForAi>();
        var missed = new List<decimal>();
        for (var n = 1; n <= 3; n++)
        {
            var quotes = RandomQuotes(rng);
            var worstAsset = quotes.Values.MinBy(q => q.GrowthFactor)!.Symbol;
            var allocation = Assets.ToDictionary(a => a, a => a == worstAsset ? 100 : 0);
            var r = ScoreRound(n, allocation, quotes, RandomDecisionTime(rng));
            rounds.Add(r);
            missed.Add(PortfolioCalculator.Value(Money.Of(100_000m), [new AllocationLine(quotes.Values.MaxBy(q => q.GrowthFactor)!.Symbol, Percentage.Of(100))], quotes).Final.Amount - 100_000m);
        }
        return Assemble("worst", rounds, missed);
    }

    /// <summary>Scenario 2: an even 25/25/25/25 split every round, regardless of how the market moves.</summary>
    private static GameSummaryForAi BuildEqualSplitEveryRound(int seed)
    {
        var rng = new Random(seed);
        var rounds = new List<RoundSummaryForAi>();
        for (var n = 1; n <= 3; n++)
        {
            var quotes = RandomQuotes(rng);
            var allocation = Assets.ToDictionary(a => a, _ => 25);
            rounds.Add(ScoreRound(n, allocation, quotes, RandomDecisionTime(rng)));
        }
        return Assemble("equal", rounds, [0m, 0m, 0m]);
    }

    /// <summary>Scenario 3: 100% of every round goes into the same single asset, whatever it does.</summary>
    private static GameSummaryForAi BuildAllInOneAsset(int seed, string asset)
    {
        var rng = new Random(seed);
        var rounds = new List<RoundSummaryForAi>();
        for (var n = 1; n <= 3; n++)
        {
            var quotes = RandomQuotes(rng);
            var allocation = Assets.ToDictionary(a => a, a => a == asset ? 100 : 0);
            rounds.Add(ScoreRound(n, allocation, quotes, RandomDecisionTime(rng)));
        }
        return Assemble("all-in", rounds, [0m, 0m, 0m]);
    }

    /// <summary>Scenario 4: the player always picks that round's best-performing asset - guarantees
    /// score 1000 every round.</summary>
    private static GameSummaryForAi BuildBestEveryRound(int seed)
    {
        var rng = new Random(seed);
        var rounds = new List<RoundSummaryForAi>();
        for (var n = 1; n <= 3; n++)
        {
            var quotes = RandomQuotes(rng);
            var bestAsset = quotes.Values.MaxBy(q => q.GrowthFactor)!.Symbol;
            var allocation = Assets.ToDictionary(a => a, a => a == bestAsset ? 100 : 0);
            rounds.Add(ScoreRound(n, allocation, quotes, RandomDecisionTime(rng)));
        }
        return Assemble("best", rounds, [0m, 0m, 0m]);
    }

    /// <summary>Scenario 5: fully random allocation each round - no engineered pattern.</summary>
    private static GameSummaryForAi BuildRandomMix(int seed)
    {
        var rng = new Random(seed);
        var rounds = new List<RoundSummaryForAi>();
        for (var n = 1; n <= 3; n++)
        {
            var quotes = RandomQuotes(rng);
            var allocation = RandomAllocation(rng);
            rounds.Add(ScoreRound(n, allocation, quotes, RandomDecisionTime(rng)));
        }
        return Assemble("mixed", rounds, [0m, 0m, 0m]);
    }

    /// <summary>Four random non-negative integers that sum to exactly 100.</summary>
    private static Dictionary<string, int> RandomAllocation(Random rng)
    {
        var cuts = new[] { rng.Next(0, 101), rng.Next(0, 101), rng.Next(0, 101) }.OrderBy(x => x).ToArray();
        var weights = new[] { cuts[0], cuts[1] - cuts[0], cuts[2] - cuts[1], 100 - cuts[2] };
        return Assets.Zip(weights, (a, w) => (a, w)).ToDictionary(t => t.a, t => t.w);
    }

    /// <summary>Forwards log lines to the test's own output so a fallback's reason (invalid shape,
    /// hallucinated number, network failure) is visible in the diagnostic run - NullLogger would
    /// silently swallow exactly the detail this diagnostic exists to show.</summary>
    private sealed class TestOutputLogger<T>(ITestOutputHelper output) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => output.WriteLine($"  [{logLevel}] {formatter(state, exception)}{(exception is null ? "" : $" :: {exception.GetType().Name}: {exception.Message}")}");
    }
}
