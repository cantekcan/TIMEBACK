namespace Timeback.Application.Games;

public sealed record GetGameResultQuery(Guid GameId, string? GameToken);
