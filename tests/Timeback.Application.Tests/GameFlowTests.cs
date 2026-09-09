using FluentAssertions;
using Timeback.Application.Common;
using Timeback.Application.Games;
using Timeback.Application.Leaderboard;
using Timeback.Domain.Games;
using Timeback.Domain.Scoring;

namespace Timeback.Application.Tests;

public class GameFlowTests
{
    private readonly InMemoryGameRepository _games = new();
    private readonly InMemoryLeaderboardRepository _board = new();
    private readonly FakeMarketDataStore _market = new();
    private readonly FakeInflationStore _inflation = new();
    private readonly FakeTokenFactory _tokens = new();
    private readonly FixedClock _clock = new(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private GamePlayService Play => new(_market, _inflation, _clock);

    private async Task<(Guid Id, string Token)> StartGame()
    {
        var res = await new StartGameHandler(_games, _market, new RoundDatePlanner(_market), _tokens, _clock)
            .Handle(new StartGameCommand(), default);
        return (res.GameId, res.GameToken);
    }

    private Task<RoundResultView> Submit(Guid id, string token, int n, params (string s, int w)[] alloc) =>
        new SubmitAllocationHandler(_games, _market, Play, _tokens, _clock).Handle(
            new SubmitAllocationCommand(id, token, n, alloc.Select(a => new AllocationInput(a.s, a.w)).ToList()), default);

    [Fact]
    public async Task Full_playthrough_completes_and_is_leaderboard_eligible()
    {
        var (id, token) = await StartGame();

        for (var n = 1; n <= RoundScoring.TotalRounds; n++)
        {
            if (n > 1)
                await new GetCurrentRoundHandler(_games, _market, Play, _tokens, _clock)
                    .Handle(new GetCurrentRoundQuery(id, token), default);
            var result = await Submit(id, token, n, ("GOLD", 100), ("BTC", 0));
            result.Score.Should().Be(1000);
        }

        var view = await new GetGameResultHandler(_games, _board, Play, _tokens, new EchoAi())
            .Handle(new GetGameResultQuery(id, token), default);
        view.Status.Should().Be("Completed");
        view.FinalScore.Should().Be(RoundScoring.MaxGameScore);
        view.AiCommentary.Should().Be($"score {RoundScoring.MaxGameScore}");
        view.AiModel.Should().Be("echo/fake");

        var score = await new SaveLeaderboardEntryHandler(_games, _board, _tokens, _clock)
            .Handle(new SaveLeaderboardEntryCommand(id, token, "Neo"), default);
        score.Should().Be(RoundScoring.MaxGameScore);
    }

    [Fact]
    public async Task Wrong_session_token_is_a_404()
    {
        var (id, _) = await StartGame();
        var act = () => Submit(id, "not-the-token", 1, ("GOLD", 100));
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Leaderboard_score_cannot_be_forged_only_nickname_is_accepted()
    {
        var (id, token) = await StartGame();
        for (var n = 1; n <= RoundScoring.TotalRounds; n++)
        {
            await new GetCurrentRoundHandler(_games, _market, Play, _tokens, _clock)
                .Handle(new GetCurrentRoundQuery(id, token), default);
            // deliberately weak play -> low score
            await Submit(id, token, n, ("BTC", 100), ("GOLD", 0));
        }
        await new GetGameResultHandler(_games, _board, Play, _tokens, new EchoAi())
            .Handle(new GetGameResultQuery(id, token), default);

        var savedScore = await new SaveLeaderboardEntryHandler(_games, _board, _tokens, _clock)
            .Handle(new SaveLeaderboardEntryCommand(id, token, "Faker"), default);

        var game = await _games.GetAsync(id, default);
        savedScore.Should().Be(game!.FinalScore);      // server score, not a client value
        _board.Entries.Single().Score.Should().Be(game.FinalScore);
    }

    [Fact]
    public async Task Result_is_not_available_before_the_game_finishes()
    {
        var (id, token) = await StartGame();
        var act = () => new GetGameResultHandler(_games, _board, Play, _tokens, new EchoAi())
            .Handle(new GetGameResultQuery(id, token), default);
        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Late_submission_returns_the_auto_locked_zero_score_result_instead_of_an_error()
    {
        // A submit that arrives after the deadline (e.g. the request was in flight when the
        // countdown hit zero) must never strand the player on an error screen - the round is
        // simply resolved as an auto-lock, same as if nobody had submitted at all.
        var (id, token) = await StartGame();
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);

        var result = await Submit(id, token, 1, ("GOLD", 100));

        result.AutoLocked.Should().BeTrue();
        result.Score.Should().Be(0);
        result.Assets.Should().BeEmpty(); // no synthetic "cash" line, no allocation at all

        var game = await _games.GetAsync(id, default);
        game!.RoundByNumber(1).Status.Should().Be(RoundStatus.Locked);
    }

    [Fact]
    public async Task Empty_allocation_before_the_deadline_is_rejected_not_treated_as_an_investment()
    {
        // The client sends an empty allocation to mean "the timer ran out, nothing was locked in" -
        // it must never be usable to skip investing while the round is still genuinely open.
        var (id, token) = await StartGame();

        var act = () => Submit(id, token, 1);

        await act.Should().ThrowAsync<Timeback.Domain.Common.DomainException>();
    }

    [Fact]
    public async Task Empty_allocation_after_the_deadline_resolves_as_no_investment()
    {
        // This is exactly what the frontend's timer sends when it runs out without a "Kilitle"
        // click - never the player's live, unconfirmed slider values.
        var (id, token) = await StartGame();
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);

        var result = await Submit(id, token, 1);

        result.AutoLocked.Should().BeTrue();
        result.Score.Should().Be(0);
        result.Assets.Should().BeEmpty();
    }

    [Fact]
    public async Task A_real_submission_is_never_overwritten_by_a_later_empty_timeout_submission()
    {
        // Mirrors the client-side race: a manual "Kilitle" click that already succeeded must never
        // be clobbered by the timer's own empty auto-submit landing shortly after (e.g. both
        // requests were briefly in flight together).
        var (id, token) = await StartGame();
        var real = await Submit(id, token, 1, ("GOLD", 100));

        var late = await Submit(id, token, 1); // empty - as if the timer's own call landed after

        late.Should().BeEquivalentTo(real);
        late.AutoLocked.Should().BeFalse();
    }

    [Fact]
    public async Task GetGameResult_never_mentions_the_internal_cash_symbol_for_an_autolocked_round()
    {
        // Round 1: real, on-time submission. Round 2: begins, then its deadline passes with
        // nothing ever locked in. Round 3: real, on-time submission.
        var (id, token) = await StartGame();
        var getCurrentRound = new GetCurrentRoundHandler(_games, _market, Play, _tokens, _clock);

        await Submit(id, token, 1, ("GOLD", 100));

        await getCurrentRound.Handle(new GetCurrentRoundQuery(id, token), default);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);

        // This call's own EnforceDeadlinesAsync catches round 2's expiry before beginning round 3.
        await getCurrentRound.Handle(new GetCurrentRoundQuery(id, token), default);
        await Submit(id, token, 3, ("GOLD", 100));

        var view = await new GetGameResultHandler(_games, _board, Play, _tokens, new EchoAi())
            .Handle(new GetGameResultQuery(id, token), default);

        view.Status.Should().Be("Completed");
        var round2 = view.Rounds.Single(r => r.Number == 2);
        round2.AutoLocked.Should().BeTrue();
        round2.Score.Should().Be(0);
        round2.Assets.Should().BeEmpty();

        // Nothing in any round's result may leak the internal sentinel or imply a real decision.
        view.Rounds.SelectMany(r => r.Assets).Should().NotContain(a => a.Symbol.Contains("CASH"));
        view.Rounds.SelectMany(r => r.Assets).Should().NotContain(a => a.Symbol.Contains("cash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Duplicate_submission_for_a_round_returns_the_original_locked_result()
    {
        // Once a round is locked, resubmitting (a retry, or the manual-click/auto-submit race)
        // must be idempotent, not an error - and it can never rescoring the round with new data.
        var (id, token) = await StartGame();
        var first = await Submit(id, token, 1, ("GOLD", 100));
        var second = await Submit(id, token, 1, ("BTC", 100));

        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task Round_view_holding_period_matches_the_real_backend_horizon()
    {
        // The frontend must never guess the holding period from dates - it has to come straight from
        // the same entry/valuation gap the domain itself scores the round with.
        var response = await new StartGameHandler(_games, _market, new RoundDatePlanner(_market), _tokens, _clock)
            .Handle(new StartGameCommand(), default);

        var game = await _games.GetAsync(response.GameId, default);
        var round = game!.RoundByNumber(1);
        var actualHorizonYears = round.ValuationDate.Year - round.EffectiveMarketDate.Year;

        response.CurrentRound.HoldingPeriodYears.Should().Be(actualHorizonYears);
        response.CurrentRound.HoldingPeriodYears.Should().BeInRange(1, 4);
    }
}
