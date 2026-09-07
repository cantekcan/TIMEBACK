using Microsoft.EntityFrameworkCore;
using Timeback.Application.Abstractions;
using Timeback.Application.Common;
using Timeback.Domain.Games;
using Timeback.Domain.Leaderboard;

namespace Timeback.Infrastructure.Persistence;

internal sealed class GameRepository(TimebackDbContext db) : IGameRepository
{
    public async Task AddAsync(Game game, CancellationToken ct) => await db.Games.AddAsync(game, ct);

    public Task<Game?> GetAsync(Guid gameId, CancellationToken ct) =>
        db.Games
            .Include(g => g.Rounds).ThenInclude(r => r.Allocations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId, ct);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException(
                "The game changed while your request was being processed. Please retry.");
        }
    }
}

internal sealed class LeaderboardRepository(TimebackDbContext db) : ILeaderboardRepository
{
    public Task<bool> ExistsForGameAsync(Guid gameId, CancellationToken ct) =>
        db.LeaderboardEntries.AnyAsync(x => x.GameId == gameId, ct);

    public async Task AddAsync(LeaderboardEntry entry, CancellationToken ct) =>
        await db.LeaderboardEntries.AddAsync(entry, ct);

    public async Task<IReadOnlyList<LeaderboardEntry>> GetTopAsync(int count, CancellationToken ct) =>
        await db.LeaderboardEntries
            .AsNoTracking()
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.CreatedAtUtc)
            .Take(count)
            .ToListAsync(ct);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_leaderboard_entries_GameId") == true)
        {
            throw new ConflictException("This game is already on the leaderboard.");
        }
    }
}
