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

/// <summary>One of the three rounds of a game. Owned by the <see cref="Game"/> aggregate root.</summary>
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

        foreach (var line in allocation.Lines)
            _allocations.Add(new RoundAllocation(line.Symbol, line.Weight.Value));

        var valuation = PortfolioCalculator.Value(startingCapital, allocation.Lines, quotes);
        var outcome = RoundScoring.Score(valuation, quotes, cpiThen, cpiNow);
        // Real submission time, from the server's own clock - never the client's countdown - decides
        // the speed bonus. The bonus is measured against the real 15-second selection window
        // (StartedAtUtc + 15s), not EndsAtUtc - EndsAtUtc also carries the network-grace allowance,
        // which must stay pure submission tolerance and never buy extra time-bonus points.
        var windowEndUtc = EndsAtUtc!.Value - Game.NetworkGrace;
        outcome = RoundScoring.ApplyTimeBonus(outcome, windowEndUtc, nowUtc);
        Finish(outcome, nowUtc, autoLocked: false);
    }

    /// <summary>Deadline passed with no allocation ever locked in: the round closes with no investment
    /// at all - no allocation lines, no growth, score 0. There is no synthetic "cash" asset standing
    /// in for a choice the player never made; a reader (a person or the AI commentator) later must see
    /// this as "no investment happened", not as a decision to hold cash.</summary>
    internal void AutoLock(
        IReadOnlyDictionary<string, AssetQuote> quotes,
        decimal cpiThen,
        decimal cpiNow,
        Money startingCapital,
        DateTime nowUtc)
    {
        if (Status != RoundStatus.AwaitingSubmission) return;

        // Nothing invested -> nothing grew: the player's money is worth exactly what it started at
        // (before inflation). RoundScoring still needs the real quotes to report what the best/worst
        // possible outcome would have been, so the player can see what they missed.
        var valuation = new PortfolioValuation(startingCapital, startingCapital, []);
        var outcome = RoundScoring.Score(valuation, quotes, cpiThen, cpiNow) with { InvestmentScore = 0, TimeBonus = 0, Score = 0 };
        Finish(outcome, nowUtc, autoLocked: true);
    }

    private void Finish(RoundOutcome outcome, DateTime nowUtc, bool autoLocked)
    {
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
