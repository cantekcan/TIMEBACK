using Timeback.Domain.Common;
using Timeback.Domain.Scoring;
using Timeback.Domain.ValueObjects;

namespace Timeback.Domain.Games;

public enum GameStatus
{
    InProgress = 0,
    Completed = 1
}

/// <summary>
/// Aggregate root for a single play-through: <see cref="RoundScoring.TotalRounds"/> rounds, a fixed
/// starting capital, a final score and an optional AI commentary. All timing and scoring authority
/// lives here - the API only relays inputs.
/// </summary>
public sealed class Game : AggregateRoot
{
    public const decimal StartingCapitalAmount = 100_000m;

    public static readonly TimeSpan SelectionWindow = TimeSpan.FromSeconds(15);
    /// <summary>Extra server-side slack for network latency before a submission is rejected as late.</summary>
    public static readonly TimeSpan NetworkGrace = TimeSpan.FromSeconds(2);

    public GameStatus Status { get; private set; } = GameStatus.InProgress;

    /// <summary>SHA-256 (hex) of the opaque session token handed to the client once at creation.
    /// Every mutating call must present the matching token - the game id alone is not a capability.</summary>
    public string GameTokenHash { get; private set; } = null!;

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public int? FinalScore { get; private set; }
    public string? AiCommentary { get; private set; }
    /// <summary>The Gemini model that actually generated <see cref="AiCommentary"/> (e.g.
    /// "gemini-3.5-flash-lite" or, on fallback, "gemini-3.1-flash-lite"), or null when the
    /// deterministic commentator produced it.</summary>
    public string? AiModel { get; private set; }

    private readonly List<Round> _rounds = [];

    /// <summary>The rounds in play order. Backed directly by the collection field (EF navigation).</summary>
    public IReadOnlyList<Round> Rounds => _rounds;

    private IEnumerable<Round> OrderedRounds => _rounds.OrderBy(r => r.Number);

    public Money StartingCapital => Money.Of(StartingCapitalAmount);

    private Game() { } // EF

    private Game(DateTime nowUtc, string gameTokenHash)
    {
        CreatedAtUtc = nowUtc;
        GameTokenHash = gameTokenHash;
    }

    /// <summary>Creates a game with all five rounds' dates pre-resolved (rounds still <see cref="RoundStatus.Pending"/>).</summary>
    public static Game Start(DateTime nowUtc, string gameTokenHash, IReadOnlyList<RoundDates> roundDates)
    {
        if (string.IsNullOrWhiteSpace(gameTokenHash))
            throw new DomainException("A game token hash is required.");
        if (roundDates.Count != RoundScoring.TotalRounds)
            throw new DomainException($"A game needs exactly {RoundScoring.TotalRounds} rounds.");

        var game = new Game(nowUtc, gameTokenHash);
        var number = 1;
        foreach (var d in roundDates)
            game._rounds.Add(new Round(game.Id, number++, d.RequestedDate, d.EffectiveMarketDate, d.ValuationDate));
        return game;
    }

    /// <summary>Constant-time-ish check that the caller holds this game's session token.</summary>
    public bool MatchesToken(string providedTokenHash)
        => !string.IsNullOrEmpty(providedTokenHash)
           && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
               System.Text.Encoding.ASCII.GetBytes(GameTokenHash),
               System.Text.Encoding.ASCII.GetBytes(providedTokenHash));

    public Round CurrentRound()
    {
        var round = _rounds.OrderBy(r => r.Number).FirstOrDefault(r => r.Status != RoundStatus.Locked);
        return round ?? throw new DomainException("All rounds are complete.");
    }

    public Round RoundByNumber(int number)
        => _rounds.SingleOrDefault(r => r.Number == number)
           ?? throw new DomainException($"Round {number} does not exist in this game.");

    /// <summary>Starts the deadline clock for the current round. Idempotent per round.</summary>
    public Round BeginCurrentRound(DateTime nowUtc)
    {
        EnsureInProgress();
        var round = CurrentRound();
        if (round.Status == RoundStatus.Pending)
            round.Begin(nowUtc, SelectionWindow, NetworkGrace);
        return round;
    }

    public void SubmitRound(
        int number,
        AllocationSet allocation,
        IReadOnlyDictionary<string, AssetQuote> quotes,
        decimal cpiThen,
        decimal cpiNow,
        DateTime nowUtc)
    {
        EnsureInProgress();
        var round = RoundByNumber(number);
        round.Submit(allocation, quotes, cpiThen, cpiNow, StartingCapital, nowUtc);
        CompleteIfFinished(nowUtc);
    }

    public void AutoLockRound(
        int number,
        IReadOnlyDictionary<string, AssetQuote> quotes,
        decimal cpiThen,
        decimal cpiNow,
        DateTime nowUtc)
    {
        EnsureInProgress();
        RoundByNumber(number).AutoLock(quotes, cpiThen, cpiNow, StartingCapital, nowUtc);
        CompleteIfFinished(nowUtc);
    }

    public void AttachAiCommentary(string commentary, string? model)
    {
        if (Status != GameStatus.Completed)
            throw new DomainException("Commentary can only be attached to a completed game.");
        AiCommentary = commentary;
        AiModel = model;
    }

    private void CompleteIfFinished(DateTime nowUtc)
    {
        if (_rounds.Any(r => r.Status != RoundStatus.Locked)) return;
        Status = GameStatus.Completed;
        CompletedAtUtc = nowUtc;
        FinalScore = _rounds.Sum(r => r.Result!.Score);
    }

    private void EnsureInProgress()
    {
        if (Status != GameStatus.InProgress)
            throw new DomainException("This game is already complete.");
    }
}

/// <summary>The three dates that define a round, resolved by the application layer against the market calendar.</summary>
public readonly record struct RoundDates(DateOnly RequestedDate, DateOnly EffectiveMarketDate, DateOnly ValuationDate);
