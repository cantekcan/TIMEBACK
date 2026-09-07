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
            game.SubmitRound(n, Alloc(100, 0), Quotes(), 100, 100, now.AddSeconds(1));
        }

        game.Status.Should().Be(GameStatus.Completed);
        game.FinalScore.Should().Be(game.Rounds.Sum(r => r.Result!.Score));
        game.FinalScore.Should().Be(RoundScoring.MaxGameScore); // GOLD was the best pick every round
    }

    [Fact]
    public void Auto_lock_produces_a_zero_growth_cash_result_when_the_player_never_submits()
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
