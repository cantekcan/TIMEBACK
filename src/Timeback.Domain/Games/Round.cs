using Timeback.Domain.Common;
using Timeback.Domain.Scoring;
using Timeback.Domain.ValueObjects;

namespace Timeback.Domain.Games;

public enum RoundStatus
{
    /// <summary>Dates assigned, not yet shown to the player - the clock has not started.</summary>
    Pending = 0,
    /// <summary>Shown to the player; the server deadline is running.</summary>
    AwaitingSubmission = 1,
    /// <summary>Allocation received (or deadline auto-locked) and scored. Immutable.</summary>
    Locked = 2
}

/// <summary>One of the five rounds of a game. Owned by the <see cref="Game"/> aggregate root.</summary>
public sealed class Round : Entity
{
    public Guid GameId { get; private set; }
    public int Number { get; private set; }

    /// <summary>The date the player is shown.</summary>
    public DateOnly RequestedDate { get; private set; }
    /// <summary>The nearest valid trading day at or before <see cref="RequestedDate"/> used for entry prices.</summary>
    public DateOnly EffectiveMarketDate { get; private set; }
    /// <summary>The "today" date the portfolio is valued at (latest date with full market data).</summary>
    public DateOnly ValuationDate { get; private set; }

    public RoundStatus Status { get; private set; } = RoundStatus.Pending;
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? EndsAtUtc { get; private set; }
    public DateTime? SubmittedAtUtc { get; private set; }
    public bool AutoLocked { get; private set; }

    private readonly List<RoundAllocation> _allocations = [];
    public IReadOnlyList<RoundAllocation> Allocations => _allocations;

    public RoundResult? Result { get; private set; }

    private Round() { } // EF

    internal Round(Guid gameId, int number, DateOnly requestedDate, DateOnly effectiveMarketDate, DateOnly valuationDate)
    {
        GameId = gameId;
        Number = number;
        RequestedDate = requestedDate;
        EffectiveMarketDate = effectiveMarketDate;
        ValuationDate = valuationDate;
    }

    internal void Begin(DateTime nowUtc, TimeSpan selectionWindow, TimeSpan networkGrace)
    {
        if (Status != RoundStatus.Pending)
            throw new DomainException($"Round {Number} has already started.");
        Status = RoundStatus.AwaitingSubmission;
        StartedAtUtc = nowUtc;
        EndsAtUtc = nowUtc + selectionWindow + networkGrace;
    }

    public bool IsExpired(DateTime nowUtc) => EndsAtUtc is { } ends && nowUtc > ends;

    internal void Submit(
        AllocationSet allocation,
        IReadOnlyDictionary<string, AssetQuote> quotes,
        decimal cpiThen,
        decimal cpiNow,
        Money startingCapital,
        DateTime nowUtc)
    {
        if (Status != RoundStatus.AwaitingSubmission)
            throw new DomainException($"Round {Number} is not accepting submissions.");
        if (IsExpired(nowUtc))
            throw new DomainException($"Round {Number} deadline has passed.");

        Lock(allocation, quotes, cpiThen, cpiNow, startingCapital, nowUtc, autoLocked: false);
    }

    /// <summary>Deadline passed with no submission: lock with an all-cash (zero-growth) portfolio, score 0-ish.</summary>
    internal void AutoLock(
        IReadOnlyDictionary<string, AssetQuote> quotes,
        decimal cpiThen,
        decimal cpiNow,
        Money startingCapital,
        DateTime nowUtc)
    {
        if (Status != RoundStatus.AwaitingSubmission) return;

        // Cash: model as a synthetic quote with growth factor 1 so the engine stays uniform.
        var cashSymbol = "__CASH__";
        var withCash = new Dictionary<string, AssetQuote>(quotes) { [cashSymbol] = new(cashSymbol, 1m, 1m) };
        var allocation = AllocationSet.Create([(cashSymbol, 100)], new HashSet<string> { cashSymbol });
        Lock(allocation, withCash, cpiThen, cpiNow, startingCapital, nowUtc, autoLocked: true);
    }

    private void Lock(
        AllocationSet allocation,
        IReadOnlyDictionary<string, AssetQuote> quotes,
        decimal cpiThen,
        decimal cpiNow,
        Money startingCapital,
        DateTime nowUtc,
        bool autoLocked)
    {
        _allocations.Clear();
        foreach (var line in allocation.Lines)
            _allocations.Add(new RoundAllocation(line.Symbol, line.Weight.Value));

        var valuation = PortfolioCalculator.Value(startingCapital, allocation.Lines, quotes);
        var realQuotes = quotes.Where(kv => kv.Key != "__CASH__").ToDictionary(kv => kv.Key, kv => kv.Value);
        var scoringQuotes = realQuotes.Count > 0 ? realQuotes : quotes;
        var outcome = RoundScoring.Score(valuation, scoringQuotes, cpiThen, cpiNow);
        if (autoLocked)
            outcome = outcome with { Score = 0 }; // forfeiting the round forfeits its points

        Result = RoundResult.From(outcome);
        Status = RoundStatus.Locked;
        SubmittedAtUtc = nowUtc;
        AutoLocked = autoLocked;
    }
}

/// <summary>Owned value: one persisted allocation line of a locked round.</summary>
public sealed class RoundAllocation
{
    public string Symbol { get; private set; } = null!;
    public int Weight { get; private set; }
    private RoundAllocation() { }
    public RoundAllocation(string symbol, int weight) { Symbol = symbol; Weight = weight; }
}
