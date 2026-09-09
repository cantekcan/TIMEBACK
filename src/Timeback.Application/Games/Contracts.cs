using Timeback.Domain.Assets;
using Timeback.Domain.Games;

namespace Timeback.Application.Games;

// The response shapes for the Games endpoints, plus how each one is built from the domain model.
// Kept together in one file because both directions (the shape, and how to fill it) are always
// read side by side, and none of these mappings is more than a few fields of straight copying.

public sealed record AssetView(string Symbol, string DisplayName, string AssetClass);

public sealed record RoundView(
    int Number,
    int TotalRounds,
    string RequestedDate,
    decimal StartingCapital,
    string? StartedAtUtc,
    int SelectionWindowSeconds,
    int HoldingPeriodYears,
    IReadOnlyList<AssetView> Assets);

public sealed record StartGameResponse(Guid GameId, string GameToken, RoundView CurrentRound);

public sealed record AssetResultView(
    string Symbol, decimal GrowthFactor, decimal ReturnFraction);

public sealed record RoundResultView(
    int Number,
    bool AutoLocked,
    decimal StartingCapital,
    decimal FinalValue,
    decimal NominalReturnFraction,
    decimal RealReturnFraction,
    decimal InflationFraction,
    decimal BestPossibleValue,
    string BestPossibleSymbol,
    decimal WorstPossibleValue,
    decimal MissedGain,
    int Score,
    // Null only for a round scored before this breakdown existed - see RoundResult.InvestmentScore.
    int? InvestmentScore,
    int? TimeBonus,
    IReadOnlyList<AssetResultView> Assets);

public sealed record GameResultView(
    Guid GameId,
    string Status,
    int? FinalScore,
    int MaxScore,
    IReadOnlyList<RoundResultView> Rounds,
    string? AiCommentary,
    string? AiModel,
    bool OnLeaderboard);

internal static class ViewMapping
{
    private const string DateFmt = "yyyy-MM-dd";

    public static AssetView ToView(this Asset a) => new(a.Symbol, a.DisplayName, a.Class.ToString());

    public static RoundView ToView(this Round r, IReadOnlyList<Asset> assets) => new(
        r.Number,
        Domain.Scoring.RoundScoring.TotalRounds,
        r.RequestedDate.ToString(DateFmt),
        Game.StartingCapitalAmount,
        r.StartedAtUtc?.ToString("O"),
        (int)Game.SelectionWindow.TotalSeconds,
        r.ValuationDate.Year - r.EffectiveMarketDate.Year, // both are the 1st of their month; AddYears(h) keeps entry's month, so this is exactly h
        assets.Select(ToView).ToList());

    public static RoundResultView ToResultView(this Round r)
    {
        var res = r.Result!;
        return new RoundResultView(
            r.Number, r.AutoLocked,
            res.StartingCapital, res.FinalValue,
            res.NominalReturnFraction, res.RealReturnFraction, res.InflationFraction,
            res.BestPossibleValue, res.BestPossibleSymbol, res.WorstPossibleValue, res.MissedGain, res.Score,
            res.InvestmentScore, res.TimeBonus,
            res.Assets.Select(a => new AssetResultView(
                a.Symbol, a.GrowthFactor, a.GrowthFactor - 1m)).ToList());
    }

    public static GameResultView ToResultView(this Game g, bool onLeaderboard) => new(
        g.Id,
        g.Status.ToString(),
        g.FinalScore,
        Domain.Scoring.RoundScoring.MaxGameScore,
        g.Rounds.Where(r => r.Result is not null).OrderBy(r => r.Number).Select(ToResultView).ToList(),
        g.AiCommentary,
        g.AiModel,
        onLeaderboard);
}
