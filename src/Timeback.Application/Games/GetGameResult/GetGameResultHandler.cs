using Timeback.Application.Abstractions;
using Timeback.Application.Common;
using Timeback.Domain.Games;

namespace Timeback.Application.Games;

public sealed class GetGameResultHandler(
    IGameRepository games,
    ILeaderboardRepository leaderboard,
    GamePlayService play,
    IGameTokenFactory tokens,
    IAiCommentator ai)
{
    public async Task<GameResultView> Handle(GetGameResultQuery request, CancellationToken ct)
    {
        // A missing game and a wrong token look identical (404) so game ids can't be probed for existence.
        var game = await games.GetAsync(request.GameId, ct);
        if (game is null || string.IsNullOrWhiteSpace(request.GameToken) || !game.MatchesToken(tokens.HashOf(request.GameToken)))
            throw new NotFoundException($"Game {request.GameId} not found.");

        var changed = await play.EnforceDeadlinesAsync(game, ct);

        if (game.Status != GameStatus.Completed)
        {
            if (changed) await games.SaveChangesAsync(ct);
            throw new ConflictException("Game is not finished yet.");
        }

        if (game.AiCommentary is null)
        {
            var comment = await ai.CommentAsync(BuildAiSummary(game), ct);
            game.AttachAiCommentary(comment.Text, comment.Model);
            changed = true;
        }

        if (changed) await games.SaveChangesAsync(ct);

        var onBoard = await leaderboard.ExistsForGameAsync(game.Id, ct);
        return game.ToResultView(onBoard);
    }

    /// <summary>Builds the safe, already-computed summary handed to the AI - it only phrases these facts, never calculates.</summary>
    private static GameSummaryForAi BuildAiSummary(Game game)
    {
        var rounds = game.Rounds.Where(r => r.Result is not null).OrderBy(r => r.Number).ToList();
        var roundScores = rounds.Select(r => r.Result!.Score).ToList();

        var best = rounds.MaxBy(r => r.Result!.Score)!;
        var worst = rounds.MinBy(r => r.Result!.Score)!;

        var avgByAsset = rounds
            .SelectMany(r => r.Allocations)
            .GroupBy(a => a.Symbol)
            .ToDictionary(g => g.Key, g => (int)Math.Round(g.Sum(a => a.Weight) / (double)rounds.Count))
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new GameSummaryForAi(
            game.FinalScore ?? 0,
            Domain.Scoring.RoundScoring.MaxGameScore,
            roundScores,
            DescribeRound(best),
            DescribeRound(worst),
            avgByAsset,
            rounds.Sum(r => r.Result!.MissedGain),
            rounds.Select(ToRoundSummary).ToList());
    }

    /// <summary>Decision time is null (never invented) when the round was auto-locked or, defensively,
    /// if either timestamp is somehow missing - the prompt must still work without it.</summary>
    private static RoundSummaryForAi ToRoundSummary(Round r)
    {
        decimal? decisionTime = !r.AutoLocked && r.StartedAtUtc is { } started && r.SubmittedAtUtc is { } submitted
            ? (decimal)(submitted - started).TotalSeconds
            : null;

        return new RoundSummaryForAi(
            r.Number,
            r.Result!.Score,
            r.AutoLocked,
            decisionTime,
            r.Allocations.ToDictionary(a => a.Symbol, a => a.Weight));
    }

    private static string DescribeRound(Round r)
    {
        var res = r.Result!;
        var topPick = r.Allocations.OrderByDescending(a => a.Weight).FirstOrDefault();
        var pick = topPick is null ? "yatırım yapılmadı" : $"{topPick.Symbol} %{topPick.Weight}";
        return $"Round {r.Number}: {pick}, {res.Score} puan, reel {res.RealReturnFraction * 100:+0;-0}%";
    }
}
