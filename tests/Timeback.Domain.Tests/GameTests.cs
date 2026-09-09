using FluentAssertions;
using Timeback.Domain.Common;
using Timeback.Domain.Games;
using Timeback.Domain.Scoring;
using Timeback.Domain.ValueObjects;

namespace Timeback.Domain.Tests;

public class GameTests
{
    private static readonly HashSet<string> Symbols = ["GOLD", "BTC"];
    private const string TokenHash = "test-token-hash";

    private static Game NewGame(DateTime now) => Game.Start(now, TokenHash, PlannedRounds());

    private static IReadOnlyList<RoundDates> PlannedRounds() =>
        Enumerable.Range(0, RoundScoring.TotalRounds)
            .Select(i => new RoundDates(
                new DateOnly(2020, 1, 10).AddYears(i),
                new DateOnly(2020, 1, 10).AddYears(i),
                new DateOnly(2024, 1, 10)))
            .ToList();

    private static Dictionary<string, AssetQuote> Quotes() => new()
    {
        ["GOLD"] = new("GOLD", 100, 200),
        ["BTC"] = new("BTC", 100, 50),
    };

    private static AllocationSet Alloc(int gold, int btc) =>
        AllocationSet.Create([("GOLD", gold), ("BTC", btc)], Symbols);

    [Fact]
    public void Start_creates_the_planned_number_of_pending_rounds()
    {
        var game = NewGame(DateTime.UtcNow);
        game.Rounds.Should().HaveCount(RoundScoring.TotalRounds);
        game.Rounds.Should().OnlyContain(r => r.Status == RoundStatus.Pending);
        game.Status.Should().Be(GameStatus.InProgress);
    }

    [Fact]
    public void Begin_sets_a_server_deadline_that_does_not_trust_the_client()
    {
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var game = NewGame(now);

        var round = game.BeginCurrentRound(now);

        round.EndsAtUtc.Should().Be(now + Game.SelectionWindow + Game.NetworkGrace);
    }

    [Fact]
    public void Submission_after_the_deadline_is_rejected()
    {
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var game = NewGame(now);
        game.BeginCurrentRound(now);

        var act = () => game.SubmitRound(1, Alloc(50, 50), Quotes(), 100, 100, now.AddSeconds(30));

        act.Should().Throw<DomainException>().WithMessage("*deadline*");
    }

    [Fact]
    public void Playing_all_rounds_completes_the_game_and_sums_the_score()
    {
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var game = NewGame(now);

        for (var n = 1; n <= RoundScoring.TotalRounds; n++)
        {
            game.BeginCurrentRound(now);
            // Submitted the instant the round began -> full 15s remaining -> max time bonus too.
            game.SubmitRound(n, Alloc(100, 0), Quotes(), 100, 100, now);
        }

        game.Status.Should().Be(GameStatus.Completed);
        game.FinalScore.Should().Be(game.Rounds.Sum(r => r.Result!.Score));
        game.FinalScore.Should().Be(RoundScoring.MaxGameScore); // GOLD was the best pick every round
    }

    [Fact]
    public void Auto_lock_produces_a_zero_growth_no_investment_result_when_the_player_never_submits()
    {
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var game = NewGame(now);
        game.BeginCurrentRound(now);

        game.AutoLockRound(1, Quotes(), 100, 100, now.AddSeconds(30));

        var r = game.RoundByNumber(1);
        r.Status.Should().Be(RoundStatus.Locked);
        r.AutoLocked.Should().BeTrue();
        r.Result!.FinalValue.Should().Be(100_000m);
        r.Result!.Score.Should().Be(0);
        r.Result!.InvestmentScore.Should().Be(0);
        r.Result!.TimeBonus.Should().Be(0); // no time bonus on a timeout either
        // No synthetic asset stands in for a decision the player never made - there is no
        // allocation at all, not even a "cash" one.
        r.Allocations.Should().BeEmpty();
        r.Result!.Assets.Should().BeEmpty();
    }

    // -- Round score = investment (0-700) + time bonus (0-300), measured against the real 15s window,
    //    never the 2s network-grace tacked onto EndsAtUtc for submission tolerance -------------------

    [Theory]
    [InlineData(0, 300)]   // locked in instantly -> full bonus
    [InlineData(5, 200)]
    [InlineData(10, 100)]
    [InlineData(14, 20)]
    [InlineData(15, 0)]    // exactly at the real window's end -> no bonus, still on time
    [InlineData(16, 0)]    // inside the network-grace allowance -> accepted, but grace buys no bonus
    [InlineData(17, 0)]    // right at the very edge of the grace allowance -> still accepted, still 0
    public void Time_bonus_is_measured_against_the_real_window_not_the_network_grace(int elapsedSeconds, int expectedTimeBonus)
    {
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var game = NewGame(now);
        game.BeginCurrentRound(now); // EndsAtUtc = now + 15s + 2s grace

        game.SubmitRound(1, Alloc(100, 0), Quotes(), 100, 100, now.AddSeconds(elapsedSeconds));

        var res = game.RoundByNumber(1).Result!;
        res.InvestmentScore.Should().Be(RoundScoring.MaxInvestmentScore); // 700 (GOLD was the best pick)
        res.TimeBonus.Should().Be(expectedTimeBonus);
        res.Score.Should().Be(RoundScoring.MaxInvestmentScore + expectedTimeBonus);
    }

    [Fact]
    public void Worst_pick_locked_in_instantly_still_never_exceeds_the_round_max()
    {
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var game = NewGame(now);
        game.BeginCurrentRound(now);

        // BTC is the worst pick this round (100 -> 50) but submitted with the full window remaining.
        game.SubmitRound(1, Alloc(0, 100), Quotes(), 100, 100, now);

        var res = game.RoundByNumber(1).Result!;
        res.InvestmentScore.Should().Be(0);
        res.TimeBonus.Should().Be(RoundScoring.MaxTimeBonus); // 300
        res.Score.Should().Be(300);
        res.Score.Should().BeLessThanOrEqualTo(RoundScoring.MaxRoundScore);
    }

    [Fact]
    public void Three_rounds_of_best_pick_at_full_speed_cap_at_the_max_game_score()
    {
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var game = NewGame(now);

        for (var n = 1; n <= RoundScoring.TotalRounds; n++)
        {
            game.BeginCurrentRound(now);
            game.SubmitRound(n, Alloc(100, 0), Quotes(), 100, 100, now);
        }

        game.FinalScore.Should().Be(RoundScoring.MaxGameScore); // 3000
    }

    [Fact]
    public void Selection_window_is_fifteen_seconds()
    {
        Game.SelectionWindow.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void A_round_cannot_be_submitted_twice()
    {
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var game = NewGame(now);
        game.BeginCurrentRound(now);
        game.SubmitRound(1, Alloc(100, 0), Quotes(), 100, 100, now.AddSeconds(1));

        var act = () => game.SubmitRound(1, Alloc(0, 100), Quotes(), 100, 100, now.AddSeconds(2));
        act.Should().Throw<DomainException>().WithMessage("*not accepting submissions*");
    }

    [Fact]
    public void Rounds_cannot_be_played_out_of_order()
    {
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var game = NewGame(now);
        game.BeginCurrentRound(now);

        // Round 3 is still Pending (never begun) -> submission rejected; round 1 is the current one.
        var act = () => game.SubmitRound(3, Alloc(100, 0), Quotes(), 100, 100, now.AddSeconds(1));
        act.Should().Throw<DomainException>();
        game.CurrentRound().Number.Should().Be(1);
    }

    [Fact]
    public void A_completed_game_rejects_further_submissions()
    {
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var game = NewGame(now);
        for (var n = 1; n <= RoundScoring.TotalRounds; n++)
        {
            game.BeginCurrentRound(now);
            game.SubmitRound(n, Alloc(100, 0), Quotes(), 100, 100, now.AddSeconds(1));
        }

        var act = () => game.SubmitRound(RoundScoring.TotalRounds, Alloc(0, 100), Quotes(), 100, 100, now.AddSeconds(2));
        act.Should().Throw<DomainException>().WithMessage("*already complete*");
    }

    [Fact]
    public void Session_token_hash_must_match()
    {
        var game = NewGame(DateTime.UtcNow);
        game.MatchesToken(TokenHash).Should().BeTrue();
        game.MatchesToken("wrong").Should().BeFalse();
        game.MatchesToken("").Should().BeFalse();
    }
}
