using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Timeback.Application.Abstractions;
using Timeback.Infrastructure.Ai;
using Timeback.Infrastructure.MarketData;
using Timeback.Infrastructure.MarketData.Ingestion;
using Timeback.Infrastructure.MarketData.Providers;
using Timeback.Infrastructure.Persistence;
using Timeback.Infrastructure.Security;
using Timeback.Infrastructure.Seeding;
using Timeback.Infrastructure.Time;

namespace Timeback.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Postgres")
            ?? "Host=localhost;Port=5432;Database=timeback;Username=timeback;Password=timeback";

        services.AddDbContext<TimebackDbContext>(o => o.UseNpgsql(connectionString, npg =>
            npg.MigrationsAssembly(typeof(TimebackDbContext).Assembly.FullName)));

        services.AddMemoryCache();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IGameTokenFactory, GameTokenFactory>();

        services.AddScoped<IGameRepository, GameRepository>();
        services.AddScoped<ILeaderboardRepository, LeaderboardRepository>();
        services.AddScoped<IMarketDataStore, EfMarketDataStore>();
        services.AddScoped<IInflationStore, EfInflationStore>();

        // --- market-data ingestion --------------------------------------------------------------
        // Default provider = committed real CSVs (offline, deterministic). "yahoo" pulls live.
        var provider = config["MarketData:Provider"]?.ToLowerInvariant() ?? "embedded";
        if (provider == "yahoo")
        {
            services.AddHttpClient<IMarketDataProvider, YahooFinanceMarketDataProvider>(c =>
            {
                c.BaseAddress = new Uri("https://query1.finance.yahoo.com");
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Timeback ingestion)");
            });
            services.AddHttpClient<IInflationDataProvider, FredInflationProvider>(c =>
                c.BaseAddress = new Uri("https://fred.stlouisfed.org"));
        }
        else
        {
            services.AddSingleton<IMarketDataProvider, EmbeddedCsvMarketDataProvider>();
            services.AddSingleton<IInflationDataProvider, EmbeddedCsvInflationProvider>();
        }
        services.AddScoped<MarketDataIngestionService>();
        services.AddScoped<DatabaseSeeder>();

        // --- AI -------------------------------------------------------------------------------
        services.Configure<GeminiOptions>(config.GetSection(GeminiOptions.SectionName));
        services.AddSingleton<FallbackAiCommentator>();
        services.AddHttpClient<IAiCommentator, GeminiAiCommentator>(c =>
            c.BaseAddress = new Uri("https://generativelanguage.googleapis.com"));

        return services;
    }
}
