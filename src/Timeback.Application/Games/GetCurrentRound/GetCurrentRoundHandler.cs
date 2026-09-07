using Timeback.Application.Abstractions;
using Timeback.Application.Common;

namespace Timeback.Application.Games;

public sealed class GetCurrentRoundHandler(
    IGameRepository games,
    IMarketDataStore marketData,
    GamePlayService play,
    IGameTokenFactory tokens,
    IClock clock)
{
    public async Task<RoundView> Handle(GetCurrentRoundQuery request, CancellationToken ct)
    {
        // A missing game and a wrong token look identical (404) so game ids can't be probed for existence.
        var game = await games.GetAsync(request.GameId, ct);
        if (game is null || string.IsNullOrWhiteSpace(request.GameToken) || !game.MatchesToken(tokens.HashOf(request.GameToken)))
            throw new NotFoundException($"Game {request.GameId} not found.");

        if (await play.EnforceDeadlinesAsync(game, ct))
            await games.SaveChangesAsync(ct);

        if (game.Status == Domain.Games.GameStatus.Completed)
            throw new ConflictException("Game is already complete; fetch the result instead.");

        game.BeginCurrentRound(clock.UtcNow);
        await games.SaveChangesAsync(ct);

        var assets = await marketData.GetActiveAssetsAsync(ct);
        return game.CurrentRound().ToView(assets);
    }
}
