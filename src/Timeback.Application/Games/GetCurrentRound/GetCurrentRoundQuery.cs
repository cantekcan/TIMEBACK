namespace Timeback.Application.Games;

public sealed record GetCurrentRoundQuery(Guid GameId, string? GameToken);
