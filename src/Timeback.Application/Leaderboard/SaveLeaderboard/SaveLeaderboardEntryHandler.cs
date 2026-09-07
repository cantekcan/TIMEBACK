using FluentValidation;
using Timeback.Application.Abstractions;
using Timeback.Application.Common;
using Timeback.Domain.Games;
using Timeback.Domain.Leaderboard;

namespace Timeback.Application.Leaderboard;

public sealed class SaveLeaderboardEntryHandler(
    IGameRepository games,
    ILeaderboardRepository leaderboard,
    IGameTokenFactory tokens,
    IClock clock)
{
    private static readonly SaveLeaderboardEntryValidator Validator = new();

    public async Task<int> Handle(SaveLeaderboardEntryCommand request, CancellationToken ct)
    {
        await Validator.ValidateAndThrowAsync(request, ct);

        // A missing game and a wrong token look identical (404) so game ids can't be probed for existence.
        var game = await games.GetAsync(request.GameId, ct);
        if (game is null || string.IsNullOrWhiteSpace(request.GameToken) || !game.MatchesToken(tokens.HashOf(request.GameToken)))
            throw new NotFoundException($"Game {request.GameId} not found.");

        if (game.Status != GameStatus.Completed || game.FinalScore is null)
            throw new ConflictException("Only a completed game can be added to the leaderboard.");

        if (await leaderboard.ExistsForGameAsync(game.Id, ct))
            throw new ConflictException("This game is already on the leaderboard.");

        // The score is taken from the server-computed game, never from the request.
        var entry = new LeaderboardEntry(game.Id, Nickname.Create(request.Nickname), game.FinalScore.Value, clock.UtcNow);
        await leaderboard.AddAsync(entry, ct);
        await leaderboard.SaveChangesAsync(ct);
        return entry.Score;
    }
}
