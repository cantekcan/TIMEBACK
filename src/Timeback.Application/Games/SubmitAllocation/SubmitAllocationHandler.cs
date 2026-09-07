using FluentValidation;
using Timeback.Application.Abstractions;
using Timeback.Application.Common;
using Timeback.Domain.Games;
using Timeback.Domain.ValueObjects;

namespace Timeback.Application.Games;

public sealed class SubmitAllocationHandler(
    IGameRepository games,
    IMarketDataStore marketData,
    GamePlayService play,
    IGameTokenFactory tokens,
    IClock clock)
{
    private static readonly SubmitAllocationValidator Validator = new();

    public async Task<RoundResultView> Handle(SubmitAllocationCommand request, CancellationToken ct)
    {
        await Validator.ValidateAndThrowAsync(request, ct);

        // A missing game and a wrong token look identical (404) so game ids can't be probed for existence.
        var game = await games.GetAsync(request.GameId, ct);
        if (game is null || string.IsNullOrWhiteSpace(request.GameToken) || !game.MatchesToken(tokens.HashOf(request.GameToken)))
            throw new NotFoundException($"Game {request.GameId} not found.");

        await play.EnforceDeadlinesAsync(game, ct);

        var round = game.RoundByNumber(request.RoundNumber);

        // The round can already be Locked here - either the deadline check just above caught it
        // (this request landed a moment after the countdown ended) or this is a retried/duplicate
        // request for a round the player already submitted. Either way there is already a final,
        // immutable result for it: return that instead of failing, so a request that is merely a
        // few hundred milliseconds late never leaves the player stuck on an error.
        if (round.Status != RoundStatus.Locked)
        {
            var assets = await marketData.GetActiveAssetsAsync(ct);
            var supported = assets.Select(a => a.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var allocation = AllocationSet.Create(
                request.Allocations.Select(a => (a.Symbol, a.Weight)), supported);

            var (quotes, cpiThen, cpiNow) = await play.LoadRoundMarketAsync(round, ct);
            game.SubmitRound(request.RoundNumber, allocation, quotes, cpiThen, cpiNow, clock.UtcNow);
        }

        await games.SaveChangesAsync(ct); // may throw DbUpdateConcurrencyException -> 409 (ApiExceptionHandler)
        return game.RoundByNumber(request.RoundNumber).ToResultView();
    }
}
