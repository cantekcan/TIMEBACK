using Timeback.Domain.Leaderboard;

namespace Timeback.Application.Abstractions;

public interface ILeaderboardRepository
{
    Task<bool> ExistsForGameAsync(Guid gameId, CancellationToken ct);
    Task AddAsync(LeaderboardEntry entry, CancellationToken ct);
    Task<IReadOnlyList<LeaderboardEntry>> GetTopAsync(int count, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
