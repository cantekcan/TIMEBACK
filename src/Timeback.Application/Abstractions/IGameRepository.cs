using Timeback.Domain.Games;

namespace Timeback.Application.Abstractions;

public interface IGameRepository
{
    Task AddAsync(Game game, CancellationToken ct);
    Task<Game?> GetAsync(Guid gameId, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
