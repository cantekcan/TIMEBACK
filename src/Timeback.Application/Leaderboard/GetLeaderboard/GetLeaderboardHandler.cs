using Timeback.Application.Abstractions;

namespace Timeback.Application.Leaderboard;

public sealed class GetLeaderboardHandler(ILeaderboardRepository leaderboard)
{
    public async Task<IReadOnlyList<LeaderboardRowView>> Handle(GetLeaderboardQuery request, CancellationToken ct)
    {
        var count = Math.Clamp(request.Count, 1, 50);
        var entries = await leaderboard.GetTopAsync(count, ct);
        return entries
            .Select((e, i) => new LeaderboardRowView(i + 1, e.Nickname, e.Score, e.CreatedAtUtc.ToString("O")))
            .ToList();
    }
}
