namespace Timeback.Application.Abstractions;

/// <summary>One round's full, already-computed picture for the AI: exactly how the player split their
/// money, how long they took to decide, and what it scored. <paramref name="DecisionTimeSeconds"/> is
/// null when the round was auto-locked (deadline passed with no submission) or, defensively, if the
/// timestamps needed to compute it are ever missing - the prompt must degrade gracefully, not crash.</summary>
public sealed record RoundSummaryForAi(
    int Number,
    int Score,
    bool AutoLocked,
    decimal? DecisionTimeSeconds,
    IReadOnlyDictionary<string, int> Allocation);

/// <summary>Summary the backend hands the AI. Contains only already-computed, safe numbers - the AI never calculates.</summary>
public sealed record GameSummaryForAi(
    int FinalScore,
    int MaxScore,
    IReadOnlyList<int> RoundScores,
    string StrongestDecision,
    string WeakestDecision,
    IReadOnlyDictionary<string, int> AverageAllocationByAsset,
    decimal TotalMissedGain,
    IReadOnlyList<RoundSummaryForAi> Rounds)
{
    /// <summary>Convenience overload for callers that don't have per-round detail (e.g. older tests) -
    /// the prompt builder treats an empty list the same as "no decision-time/allocation data available".</summary>
    public GameSummaryForAi(
        int FinalScore, int MaxScore, IReadOnlyList<int> RoundScores, string StrongestDecision,
        string WeakestDecision, IReadOnlyDictionary<string, int> AverageAllocationByAsset, decimal TotalMissedGain)
        : this(FinalScore, MaxScore, RoundScores, StrongestDecision, WeakestDecision, AverageAllocationByAsset, TotalMissedGain, [])
    {
    }
}

/// <summary>A generated comment plus which model actually produced it (null when the fallback did).</summary>
public sealed record AiComment(string Text, string? Model);

public interface IAiCommentator
{
    /// <summary>Returns a short, playful Turkish comment. Must never throw - failures fall back internally.</summary>
    Task<AiComment> CommentAsync(GameSummaryForAi summary, CancellationToken ct);
}
