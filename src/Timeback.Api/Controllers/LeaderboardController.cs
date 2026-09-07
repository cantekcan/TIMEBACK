using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Timeback.Application.Leaderboard;

namespace Timeback.Api.Controllers;

public sealed record SaveLeaderboardRequest(Guid GameId, string Nickname);

[ApiController]
[Route("api/v1/leaderboard")]
[EnableRateLimiting("default")]
public sealed class LeaderboardController(
    GetLeaderboardHandler getLeaderboard,
    SaveLeaderboardEntryHandler saveEntry) : ControllerBase
{
    private string? Token => Request.Headers.TryGetValue("X-Game-Token", out var v) ? v.ToString() : null;

    [HttpGet]
    public Task<IReadOnlyList<LeaderboardRowView>> Top([FromQuery] int count = 10, CancellationToken ct = default)
        => getLeaderboard.Handle(new GetLeaderboardQuery(count), ct);

    /// <summary>Saves the caller's completed game to the leaderboard. Requires the game's
    /// <c>X-Game-Token</c>; only a nickname is accepted from the body - the score is server-side.</summary>
    [HttpPost]
    [EnableRateLimiting("mutation")]
    public async Task<ActionResult> Save([FromBody] SaveLeaderboardRequest request, CancellationToken ct)
    {
        var score = await saveEntry.Handle(new SaveLeaderboardEntryCommand(request.GameId, Token, request.Nickname), ct);
        return Ok(new { score });
    }
}
