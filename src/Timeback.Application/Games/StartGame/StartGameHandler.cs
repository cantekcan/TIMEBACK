using Timeback.Application.Abstractions;
using Timeback.Domain.Games;

namespace Timeback.Application.Games;

public sealed class StartGameHandler(
    IGameRepository games,
    IMarketDataStore marketData,
    RoundDatePlanner planner,
    IGameTokenFactory tokens,
    IClock clock)
{
    public async Task<StartGameResponse> Handle(StartGameCommand request, CancellationToken ct)
    {
        var seed = Random.Shared.Next();
        var roundDates = await planner.PlanAsync(seed, ct);

        var token = tokens.Create();
        var game = Game.Start(clock.UtcNow, token.Hash, roundDates);
        game.BeginCurrentRound(clock.UtcNow);

        await games.AddAsync(game, ct);
        await games.SaveChangesAsync(ct);

        var assets = await marketData.GetActiveAssetsAsync(ct);
        return new StartGameResponse(game.Id, token.Value, game.CurrentRound().ToView(assets));
    }
}
