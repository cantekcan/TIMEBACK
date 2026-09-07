using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Timeback.Application.Games;

namespace Timeback.Api.Controllers;

[ApiController]
[Route("api/v1/games")]
[EnableRateLimiting("default")]
public sealed class GamesController(
    StartGameHandler startGame,
    GetCurrentRoundHandler getCurrentRound,
    SubmitAllocationHandler submitAllocation,
    GetGameResultHandler getGameResult) : ControllerBase
{
    private const string TokenHeader = "X-Game-Token";
    private string? Token => Request.Headers.TryGetValue(TokenHeader, out var v) ? v.ToString() : null;

    /// <summary>Starts a new 5-round game. The response's <c>gameToken</c> must be sent as the
    /// <c>X-Game-Token</c> header on every subsequent call for this game.</summary>
    [HttpPost]
    [EnableRateLimiting("mutation")]
    public async Task<ActionResult<StartGameResponse>> Start(CancellationToken ct)
    {
        var result = await startGame.Handle(new StartGameCommand(), ct);
        return CreatedAtAction(nameof(GetResult), new { gameId = result.GameId }, result);
    }

    /// <summary>Current round (starts its clock if needed; auto-locks any expired round first).</summary>
    [HttpGet("{gameId:guid}/round")]
    public Task<RoundView> GetCurrentRound(Guid gameId, CancellationToken ct)
        => getCurrentRound.Handle(new GetCurrentRoundQuery(gameId, Token), ct);

    /// <summary>Locks the player's allocation for a round and returns the scored result.</summary>
    [HttpPost("{gameId:guid}/rounds/{roundNumber:int}/submit")]
    public Task<RoundResultView> Submit(
        Guid gameId, int roundNumber, [FromBody] IReadOnlyList<AllocationInput> allocations, CancellationToken ct)
        => submitAllocation.Handle(new SubmitAllocationCommand(gameId, Token, roundNumber, allocations), ct);

    /// <summary>Final result of a completed game, including AI commentary (generated once).</summary>
    [HttpGet("{gameId:guid}/result")]
    public Task<GameResultView> GetResult(Guid gameId, CancellationToken ct)
        => getGameResult.Handle(new GetGameResultQuery(gameId, Token), ct);
}
