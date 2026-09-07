using System.Globalization;
using System.Reflection;
using Timeback.Application.Abstractions;

namespace Timeback.Infrastructure.MarketData.Providers;

/// <summary>
/// Serves the real monthly history committed under <c>SeedData/</c>. Those CSVs were downloaded from
/// Yahoo Finance / FRED with <c>scripts/refresh-market-data.mjs</c> and checked in so the game seeds
/// deterministically and offline (CI, air-gapped). This is the DEFAULT provider.
/// </summary>
internal sealed class EmbeddedCsvMarketDataProvider : IMarketDataProvider
{
    public string Name => "embedded-csv";

    public Task<IReadOnlyList<ProviderObservation>> GetMonthlyHistoryAsync(
        string providerSymbol, DateOnly from, DateOnly to, CancellationToken ct)
        => Task.FromResult(SeedCsv.Read($"{providerSymbol}.csv", from, to));
}

internal sealed class EmbeddedCsvInflationProvider : IInflationDataProvider
{
    public string Name => "embedded-csv";

    public Task<IReadOnlyList<ProviderObservation>> GetMonthlyIndexAsync(DateOnly from, DateOnly to, CancellationToken ct)
        => Task.FromResult(SeedCsv.Read("TUR_CPI.csv", from, to));
}

internal static class SeedCsv
{
    private static readonly string Dir = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "SeedData");

    public static IReadOnlyList<ProviderObservation> Read(string file, DateOnly from, DateOnly to)
    {
        var path = Path.Combine(Dir, file);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Seed data file missing: {file}. Run scripts/refresh-market-data.mjs.", path);

        var result = new List<ProviderObservation>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            var date = DateOnly.ParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (date < from || date > to) continue;
            if (!decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
                continue;
            result.Add(new ProviderObservation(new DateOnly(date.Year, date.Month, 1), value));
        }
        return result.OrderBy(o => o.Date).ToList();
    }
}
