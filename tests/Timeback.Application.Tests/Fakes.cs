using Timeback.Application.Abstractions;
using Timeback.Domain.Assets;
using Timeback.Domain.Games;
using Timeback.Domain.Leaderboard;
using Timeback.Domain.Scoring;

namespace Timeback.Application.Tests;

public sealed class FixedClock(DateTime now) : IClock
{
    public DateTime UtcNow { get; set; } = now;
}

public sealed class FakeTokenFactory : IGameTokenFactory
{
    public GameToken Create()
    {
        var value = "tok_" + Guid.NewGuid().ToString("N");
        return new GameToken(value, HashOf(value));
    }

    public string HashOf(string token) => "hash:" + token;
}

public sealed class InMemoryGameRepository : IGameRepository
{
    private readonly Dictionary<Guid, Game> _store = new();
    public int SaveCount { get; private set; }
    public Task AddAsync(Game game, CancellationToken ct) { _store[game.Id] = game; return Task.CompletedTask; }
    public Task<Game?> GetAsync(Guid gameId, CancellationToken ct) => Task.FromResult(_store.GetValueOrDefault(gameId));
    public Task SaveChangesAsync(CancellationToken ct) { SaveCount++; return Task.CompletedTask; }
}

public sealed class InMemoryLeaderboardRepository : ILeaderboardRepository
{
    public readonly List<LeaderboardEntry> Entries = [];
    public Task<bool> ExistsForGameAsync(Guid gameId, CancellationToken ct) => Task.FromResult(Entries.Any(e => e.GameId == gameId));
    public Task AddAsync(LeaderboardEntry entry, CancellationToken ct) { Entries.Add(entry); return Task.CompletedTask; }
    public Task<IReadOnlyList<LeaderboardEntry>> GetTopAsync(int count, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<LeaderboardEntry>>(Entries.OrderByDescending(e => e.Score).Take(count).ToList());
    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class FakeMarketDataStore : IMarketDataStore
{
    // GOLD wins, BTC loses - deterministic.
    private static readonly Dictionary<string, IReadOnlyList<PricePoint>> Series = new()
    {
        ["GOLD"] = Enumerable.Range(0, 132).Select(m => new PricePoint(new DateOnly(2015, 1, 1).AddMonths(m), 100m + m)).ToList(),
        ["BTC"] = Enumerable.Range(0, 132).Select(m => new PricePoint(new DateOnly(2015, 1, 1).AddMonths(m), 100m + m * 0.2m)).ToList(),
    };

    public Task<IReadOnlyList<Asset>> GetActiveAssetsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Asset>>([
            new Asset("GOLD", "Altin", AssetClass.Commodity),
            new Asset("BTC", "Bitcoin", AssetClass.Crypto),
        ]);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<PricePoint>>> GetPriceSeriesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<PricePoint>>>(Series);

    public Task<(DateOnly Earliest, DateOnly Latest)> GetCoverageAsync(CancellationToken ct)
        => Task.FromResult((Series["GOLD"][0].Date, Series["GOLD"][^1].Date));

    public Task<DateOnly?> ResolveEffectiveMarketDateAsync(DateOnly requested, CancellationToken ct)
        => Task.FromResult<DateOnly?>(new DateOnly(requested.Year, requested.Month, 1));

    public Task<IReadOnlyDictionary<string, AssetQuote>> GetQuotesAsync(DateOnly e, DateOnly v, CancellationToken ct)
    {
        decimal At(string s, DateOnly d)
        {
            decimal last = Series[s][0].Close;
            foreach (var p in Series[s]) { if (p.Date > d) break; last = p.Close; }
            return last;
        }
        return Task.FromResult<IReadOnlyDictionary<string, AssetQuote>>(new Dictionary<string, AssetQuote>
        {
            ["GOLD"] = new("GOLD", At("GOLD", e), At("GOLD", v)),
            ["BTC"] = new("BTC", At("BTC", e), At("BTC", v)),
        });
    }
}

public sealed class FakeInflationStore : IInflationStore
{
    public Task<decimal> GetIndexAsync(DateOnly date, CancellationToken ct)
        => Task.FromResult(100m + (date.Year - 2015) * 20m);
}

public sealed class EchoAi : IAiCommentator
{
    public Task<AiComment> CommentAsync(GameSummaryForAi s, CancellationToken ct) =>
        Task.FromResult(new AiComment($"score {s.FinalScore}", "echo/fake"));
}
