using Microsoft.EntityFrameworkCore;
using Timeback.Domain.Assets;
using Timeback.Domain.Games;
using Timeback.Domain.Leaderboard;
using Timeback.Domain.MarketData;

namespace Timeback.Infrastructure.Persistence;

public sealed class TimebackDbContext(DbContextOptions<TimebackDbContext> options) : DbContext(options)
{
    public DbSet<Game> Games => Set<Game>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<DailyPrice> DailyPrices => Set<DailyPrice>();
    public DbSet<InflationIndex> InflationIndices => Set<InflationIndex>();
    public DbSet<LeaderboardEntry> LeaderboardEntries => Set<LeaderboardEntry>();

    protected override void OnModelCreating(ModelBuilder b)
        => b.ApplyConfigurationsFromAssembly(typeof(TimebackDbContext).Assembly);
}
