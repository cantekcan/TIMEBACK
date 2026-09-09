using Timeback.Application.Abstractions;
using Timeback.Domain.Games;
using Timeback.Domain.Scoring;

namespace Timeback.Application.Games;

/// <summary>
/// Shared orchestration used by the game handlers: loads the market data a round needs and enforces
/// expired deadlines server-side (a round whose clock ran out is auto-locked, whoever asks first).
/// </summary>
public sealed class GamePlayService(
    IMarketDataStore marketData,
    IInflationStore inflation,
    IClock clock)
{
    public async Task<(IReadOnlyDictionary<string, AssetQuote> Quotes, decimal CpiThen, decimal CpiNow)>
        LoadRoundMarketAsync(Round round, CancellationToken ct)
    {
        var quotes = await marketData.GetQuotesAsync(round.EffectiveMarketDate, round.ValuationDate, ct);
        var cpiThen = await inflation.GetIndexAsync(round.EffectiveMarketDate, ct);
        var cpiNow = await inflation.GetIndexAsync(round.ValuationDate, ct);
        return (quotes, cpiThen, cpiNow);
    }

    /// <summary>Auto-locks every AwaitingSubmission round whose deadline has passed. Returns true if
    /// anything changed. <paramref name="excludeRoundNumber"/> skips that one round's own deadline
    /// check - used when the caller is about to process a genuine, non-empty manual submission for
    /// that exact round, so a network/cold-start delay can never let this same sweep auto-lock the
    /// round (with an empty allocation) out from under the player's own deliberate "Kilitle" click
    /// before its real allocation ever gets a chance to be recorded (see SubmitAllocationHandler).</summary>
    public async Task<bool> EnforceDeadlinesAsync(Game game, CancellationToken ct, int? excludeRoundNumber = null)
    {
        var changed = false;
        foreach (var round in game.Rounds)
        {
            if (round.Status != RoundStatus.AwaitingSubmission || !round.IsExpired(clock.UtcNow))
                continue;
            if (round.Number == excludeRoundNumber)
                continue;

            var (quotes, cpiThen, cpiNow) = await LoadRoundMarketAsync(round, ct);
            game.AutoLockRound(round.Number, quotes, cpiThen, cpiNow, clock.UtcNow);
            changed = true;
        }
        return changed;
    }
}
