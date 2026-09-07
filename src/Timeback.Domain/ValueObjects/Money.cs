using Timeback.Domain.Common;

namespace Timeback.Domain.ValueObjects;

/// <summary>
/// An exact monetary amount. Always <see cref="decimal"/> - never floating point - because the game
/// multiplies capital by price ratios and sums results, where binary rounding drift would be visible.
/// The game is single-currency (TRY); the field is kept so a future multi-currency mode stays additive.
/// </summary>
public readonly record struct Money
{
    public const string DefaultCurrency = "TRY";

    public decimal Amount { get; }
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, string currency = DefaultCurrency)
    {
        if (amount < 0)
            throw new DomainException($"Money amount cannot be negative (got {amount}).");
        if (string.IsNullOrWhiteSpace(currency))
            throw new DomainException("Money currency is required.");
        return new Money(decimal.Round(amount, 2, MidpointRounding.ToEven), currency.ToUpperInvariant());
    }

    public static Money Zero(string currency = DefaultCurrency) => new(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return Of(Amount + other.Amount, Currency);
    }

    /// <summary>Scales the amount by a dimensionless factor (e.g. an allocation weight or a price ratio).</summary>
    public Money Scale(decimal factor)
    {
        if (factor < 0)
            throw new DomainException("Cannot scale money by a negative factor.");
        return Of(Amount * factor, Currency);
    }

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
            throw new DomainException($"Currency mismatch: {Currency} vs {other.Currency}.");
    }

    public override string ToString() => $"{Amount:0.00} {Currency}";
}
