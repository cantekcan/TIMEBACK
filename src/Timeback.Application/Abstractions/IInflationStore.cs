namespace Timeback.Application.Abstractions;

public interface IInflationStore
{
    /// <summary>CPI index level for the month containing <paramref name="date"/> (nearest available if missing).</summary>
    Task<decimal> GetIndexAsync(DateOnly date, CancellationToken ct);
}
