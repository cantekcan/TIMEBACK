using FluentValidation;

namespace Timeback.Application.Games;

public sealed record AllocationInput(string Symbol, int Weight);

public sealed record SubmitAllocationCommand(
    Guid GameId,
    string? GameToken,
    int RoundNumber,
    IReadOnlyList<AllocationInput> Allocations);

public sealed class SubmitAllocationValidator : AbstractValidator<SubmitAllocationCommand>
{
    public SubmitAllocationValidator()
    {
        RuleFor(x => x.GameId).NotEmpty();
        RuleFor(x => x.RoundNumber).InclusiveBetween(1, Domain.Scoring.RoundScoring.TotalRounds);
        RuleFor(x => x.Allocations).NotEmpty()
            .Must(a => a.Count <= 20).WithMessage("Too many allocation lines.");
        RuleForEach(x => x.Allocations).ChildRules(a =>
        {
            a.RuleFor(l => l.Symbol).NotEmpty().MaximumLength(16);
            a.RuleFor(l => l.Weight).InclusiveBetween(0, 100);
        });
    }
}
