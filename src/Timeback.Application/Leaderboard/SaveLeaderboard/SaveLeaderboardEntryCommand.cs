using FluentValidation;

namespace Timeback.Application.Leaderboard;

public sealed record SaveLeaderboardEntryCommand(Guid GameId, string? GameToken, string Nickname);

public sealed class SaveLeaderboardEntryValidator : AbstractValidator<SaveLeaderboardEntryCommand>
{
    public SaveLeaderboardEntryValidator()
    {
        RuleFor(x => x.GameId).NotEmpty();
        RuleFor(x => x.Nickname).NotEmpty().MaximumLength(16);
    }
}
