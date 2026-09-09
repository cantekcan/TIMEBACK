using Timeback.Application.Abstractions;

namespace Timeback.Infrastructure.Ai;

/// <summary>
/// Deterministic local commentary. Always available, so the game result is never blocked on an
/// external AI call. Also used verbatim as the fallback when the real provider fails.
/// </summary>
public sealed class FallbackAiCommentator : IAiCommentator
{
    public Task<AiComment> CommentAsync(GameSummaryForAi s, CancellationToken ct)
    {
        var pct = s.MaxScore == 0 ? 0 : (int)Math.Round(100.0 * s.FinalScore / s.MaxScore);

        // No allocation data at all means every round's deadline passed with nothing ever locked
        // in - "favourite asset" doesn't apply, so this gets its own line instead of slotting "no
        // investment" into a sentence shaped around a real pick.
        if (s.AverageAllocationByAsset.Count == 0)
            return Task.FromResult(new AiComment(
                $"Zaman makinesine bindin ama hiç yatırım yapmadan indin: %{pct}. Bir dahaki sefere en azından bir tahminde bulun.",
                null));

        var favourite = s.AverageAllocationByAsset.OrderByDescending(kv => kv.Value).First().Key;

        var line = pct switch
        {
            >= 85 => $"Zaman makinesini hak ettin: %{pct} isabet. {favourite} aşkın tuttu.",
            >= 60 => $"Fena değil, %{pct}. {favourite} ağırlığın işe yaramış ama en iyi turu ({s.StrongestDecision}) es geçmedin.",
            >= 35 => $"Ortalama bir kâhin: %{pct}. En zayıf anın {s.WeakestDecision} oldu, {favourite} sevgin biraz pahalıya geldi.",
            _ => $"Geçmişe döndün ve yine {favourite} dedin... %{pct}. Belki de parayı yastık altında tutmalıydın.",
        };
        return Task.FromResult(new AiComment(line, null));
    }
}
