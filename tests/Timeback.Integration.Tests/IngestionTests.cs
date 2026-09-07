using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Timeback.Application.Abstractions;
using Timeback.Domain.MarketData;
using Timeback.Infrastructure.MarketData;
using Timeback.Infrastructure.MarketData.Ingestion;
using Timeback.Infrastructure.Persistence;

namespace Timeback.Integration.Tests;

[Collection(nameof(PostgresCollection))]
public sealed class IngestionTests(PostgresFixture fx)
{
    [Fact]
    public async Task Seeding_loads_real_prices_and_cpi_for_every_asset()
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TimebackDbContext>();

        var assets = await db.Assets.CountAsync();
        assets.Should().Be(4);

        foreach (var symbol in new[] { "GOLD", "BIST100", "BTC", "SP500" })
        {
            var count = await db.DailyPrices.CountAsync(p => p.Symbol == symbol);
            count.Should().BeGreaterThan(60, $"{symbol} should have several years of monthly data");
        }

        (await db.InflationIndices.CountAsync()).Should().BeGreaterThan(60);
        (await db.DailyPrices.AllAsync(p => p.Close > 0)).Should().BeTrue();
        (await db.DailyPrices.Select(p => p.Source).Distinct().ToListAsync())
            .Should().OnlyContain(s => s.StartsWith("embedded-csv"));
    }

    [Fact]
    public async Task Ingestion_is_idempotent_running_it_again_inserts_nothing()
    {
        using var scope = fx.Factory.Services.CreateScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<MarketDataIngestionService>();
        var db = scope.ServiceProvider.GetRequiredService<TimebackDbContext>();

        var before = await db.DailyPrices.CountAsync();
        var report = await ingestion.IngestAsync(new DateOnly(2015, 1, 1), DateOnly.FromDateTime(DateTime.UtcNow), default);
        var after = await db.DailyPrices.CountAsync();

        report.PricesInserted.Should().Be(0);
        report.PricesSkippedExisting.Should().BeGreaterThan(0);
        after.Should().Be(before);
    }

    [Fact]
    public async Task Ingested_prices_never_extend_past_each_providers_real_last_observation()
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TimebackDbContext>();
        var provider = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

        foreach (var spec in AssetCatalog.All)
        {
            var raw = await provider.GetMonthlyHistoryAsync(
                spec.ProviderSymbol, new DateOnly(2015, 1, 1), DateOnly.FromDateTime(DateTime.UtcNow), default);
            var realLast = raw.Max(o => o.Date);

            var dbLast = await db.DailyPrices.Where(p => p.Symbol == spec.Symbol).MaxAsync(p => p.Date);

            dbLast.Should().Be(realLast,
                $"{spec.Symbol} must stop exactly at its provider's last real observation, never forward-filled beyond it");
        }
    }

    [Fact]
    public async Task GetCoverageAsync_never_returns_a_fake_future_date()
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TimebackDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IMarketDataStore>();
        var provider = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

        var realLastPerAsset = new List<DateOnly>();
        foreach (var spec in AssetCatalog.All)
        {
            var raw = await provider.GetMonthlyHistoryAsync(
                spec.ProviderSymbol, new DateOnly(2015, 1, 1), DateOnly.FromDateTime(DateTime.UtcNow), default);
            realLastPerAsset.Add(raw.Max(o => o.Date));
        }
        var expectedLatest = realLastPerAsset.Min(); // the earliest-ending asset caps the common coverage

        var (_, latest) = await store.GetCoverageAsync(default);

        latest.Should().Be(expectedLatest,
            "the common coverage must be the real intersection of every asset's real data, not a forward-filled future date");
    }

    [Fact]
    public async Task Reingestion_removes_stale_forward_filled_rows_beyond_the_new_real_cutoff()
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TimebackDbContext>();
        var ingestion = scope.ServiceProvider.GetRequiredService<MarketDataIngestionService>();

        // Simulate a leftover from the old forward-fill bug: a row far beyond any real observation.
        var staleDate = new DateOnly(2099, 1, 1);
        db.DailyPrices.Add(new DailyPrice("GOLD", staleDate, 123.45m, "stale-test"));
        await db.SaveChangesAsync();

        await ingestion.IngestAsync(new DateOnly(2015, 1, 1), DateOnly.FromDateTime(DateTime.UtcNow), default);

        (await db.DailyPrices.AnyAsync(p => p.Symbol == "GOLD" && p.Date == staleDate)).Should().BeFalse(
            "re-ingestion must clean up rows that fall beyond the real data's last observation");
    }
}
