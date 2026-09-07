using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Timeback.Application.Abstractions;

namespace Timeback.Infrastructure.MarketData.Providers;

/// <summary>
/// Live provider: Yahoo Finance chart API (public, no key). Used by <c>dotnet run -- ingest --live</c>
/// and by <c>scripts/refresh-market-data.mjs</c> to regenerate the committed seed CSVs. Not used at
/// runtime during play.
/// </summary>
internal sealed class YahooFinanceMarketDataProvider(HttpClient http, ILogger<YahooFinanceMarketDataProvider> logger)
    : IMarketDataProvider
{
    public string Name => "yahoo-finance";

    public async Task<IReadOnlyList<ProviderObservation>> GetMonthlyHistoryAsync(
        string providerSymbol, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var p1 = ((DateTimeOffset)from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var p2 = ((DateTimeOffset)to.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddMonths(1)).ToUnixTimeSeconds();
        var url = $"/v8/finance/chart/{Uri.EscapeDataString(providerSymbol)}?period1={p1}&period2={p2}&interval=1mo";

        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
        var timestamps = result.GetProperty("timestamp").EnumerateArray().Select(e => e.GetInt64()).ToList();
        var closes = result.GetProperty("indicators").GetProperty("quote")[0].GetProperty("close").EnumerateArray().ToList();

        var seenMonths = new HashSet<DateOnly>();
        var observations = new List<ProviderObservation>();
        for (var i = 0; i < timestamps.Count; i++)
        {
            if (closes[i].ValueKind == JsonValueKind.Null) continue;
            var dt = DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).UtcDateTime;
            var month = new DateOnly(dt.Year, dt.Month, 1);
            if (month < from || month > to || !seenMonths.Add(month)) continue;
            observations.Add(new ProviderObservation(month, closes[i].GetDecimal()));
        }

        logger.LogInformation("Yahoo {Symbol}: {Count} monthly points", providerSymbol, observations.Count);
        return observations.OrderBy(o => o.Date).ToList();
    }
}

/// <summary>Live inflation provider: FRED CSV download (public, no key). Series TURCPIALLMINMEI.</summary>
internal sealed class FredInflationProvider(HttpClient http) : IInflationDataProvider
{
    private const string Series = "TURCPIALLMINMEI";
    public string Name => "fred";

    public async Task<IReadOnlyList<ProviderObservation>> GetMonthlyIndexAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var url = $"/graph/fredgraph.csv?id={Series}&cosd={from:yyyy-MM-dd}&coed={to:yyyy-MM-dd}";
        var csv = await http.GetStringAsync(url, ct);

        var rows = new List<ProviderObservation>();
        foreach (var line in csv.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length < 2 || parts[1] is "." or "") continue;
            if (!DateOnly.TryParseExact(parts[0][..Math.Min(10, parts[0].Length)], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            if (decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
                rows.Add(new ProviderObservation(new DateOnly(date.Year, date.Month, 1), value));
        }
        return rows.OrderBy(o => o.Date).ToList();
    }
}
