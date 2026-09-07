using Timeback.Domain.Common;
using Timeback.Domain.Scoring;

namespace Timeback.Domain.ValueObjects;

/// <summary>
/// A validated portfolio split: distinct assets, each weight in [0,100], total exactly 100.
/// Constructing one is the single server-side gate for allocation rules - the client's slider
/// behaviour is pure UX and is re-checked here.
/// </summary>
public sealed class AllocationSet
{
    public const int RequiredTotal = 100;

    private readonly List<AllocationLine> _lines;
    public IReadOnlyList<AllocationLine> Lines => _lines;

    private AllocationSet(List<AllocationLine> lines) => _lines = lines;

    public static AllocationSet Create(IEnumerable<(string Symbol, int Weight)> input, ISet<string> supportedSymbols)
    {
        ArgumentNullException.ThrowIfNull(input);
        var raw = input.ToList();

        if (raw.Count == 0)
            throw new DomainException("Allocation must contain at least one asset.");

        var lines = new List<AllocationLine>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (symbolRaw, weight) in raw)
        {
            var symbol = (symbolRaw ?? string.Empty).Trim().ToUpperInvariant();
            if (symbol.Length == 0)
                throw new DomainException("Allocation contains an empty asset symbol.");
            if (!supportedSymbols.Contains(symbol))
                throw new DomainException($"Asset '{symbol}' is not supported.");
            if (!seen.Add(symbol))
                throw new DomainException($"Asset '{symbol}' appears more than once in the allocation.");

            lines.Add(new AllocationLine(symbol, Percentage.Of(weight)));
        }

        var total = lines.Sum(l => l.Weight.Value);
        if (total != RequiredTotal)
            throw new DomainException($"Allocation weights must total {RequiredTotal}% (got {total}%).");

        return new AllocationSet(lines);
    }
}
