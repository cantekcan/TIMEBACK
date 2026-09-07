using Timeback.Domain.Common;

namespace Timeback.Domain.ValueObjects;

/// <summary>
/// A whole-number allocation percentage in the inclusive range [0, 100].
/// The game restricts allocations to integers so the slider UI and the server agree exactly.
/// </summary>
public readonly record struct Percentage
{
    public int Value { get; }

    private Percentage(int value) => Value = value;

    public static Percentage Of(int value)
    {
        if (value is < 0 or > 100)
            throw new DomainException($"Percentage must be between 0 and 100 (got {value}).");
        return new Percentage(value);
    }

    /// <summary>The weight as a fraction, e.g. 25 -> 0.25.</summary>
    public decimal AsFraction => Value / 100m;

    public override string ToString() => $"{Value}%";
}
