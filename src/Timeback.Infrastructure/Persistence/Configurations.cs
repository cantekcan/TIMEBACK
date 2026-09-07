using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Timeback.Domain.Assets;
using Timeback.Domain.Games;
using Timeback.Domain.Leaderboard;
using Timeback.Domain.MarketData;

namespace Timeback.Infrastructure.Persistence;

internal sealed class AssetConfig : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> e)
    {
        e.ToTable("assets");
        e.Property(x => x.Symbol).HasMaxLength(16).IsRequired();
        e.HasIndex(x => x.Symbol).IsUnique();
        e.Property(x => x.DisplayName).HasMaxLength(64).IsRequired();
        e.Property(x => x.QuoteCurrency).HasMaxLength(3).IsRequired();
        e.Property(x => x.Class).HasConversion<string>().HasMaxLength(16);
    }
}

internal sealed class DailyPriceConfig : IEntityTypeConfiguration<DailyPrice>
{
    public void Configure(EntityTypeBuilder<DailyPrice> e)
    {
        e.ToTable("daily_prices");
        e.Property(x => x.Symbol).HasMaxLength(16).IsRequired();
        e.Property(x => x.Close).HasPrecision(28, 8);
        e.Property(x => x.Source).HasMaxLength(64).IsRequired();
        e.HasIndex(x => new { x.Symbol, x.Date }).IsUnique();     // ingestion idempotency
        e.HasIndex(x => x.Date);                                  // date-range lookups
    }
}

internal sealed class InflationConfig : IEntityTypeConfiguration<InflationIndex>
{
    public void Configure(EntityTypeBuilder<InflationIndex> e)
    {
        e.ToTable("inflation_indices");
        e.Property(x => x.Index).HasPrecision(18, 6);
        e.Property(x => x.Source).HasMaxLength(64).IsRequired();
        e.HasIndex(x => x.Month).IsUnique();
    }
}

internal sealed class LeaderboardConfig : IEntityTypeConfiguration<LeaderboardEntry>
{
    public void Configure(EntityTypeBuilder<LeaderboardEntry> e)
    {
        e.ToTable("leaderboard_entries");
        e.Property(x => x.Nickname).HasMaxLength(16).IsRequired();
        e.HasIndex(x => x.GameId).IsUnique();                     // one entry per game
        e.HasIndex(x => new { x.Score, x.CreatedAtUtc });         // top-N ordering
    }
}

internal sealed class GameConfig : IEntityTypeConfiguration<Game>
{
    public void Configure(EntityTypeBuilder<Game> e)
    {
        e.ToTable("games");
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        e.Property(x => x.AiCommentary).HasMaxLength(1024);
        e.Property(x => x.AiModel).HasMaxLength(128);
        e.Property(x => x.GameTokenHash).HasMaxLength(64).IsRequired();
        e.HasIndex(x => x.GameTokenHash).IsUnique();
        e.Ignore(x => x.StartingCapital);

        // Optimistic concurrency: PostgreSQL's built-in xmin system column. Two submissions racing
        // the same game -> the second gets DbUpdateConcurrencyException instead of a lost update.
        e.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        e.HasMany(x => x.Rounds).WithOne().HasForeignKey(r => r.GameId).OnDelete(DeleteBehavior.Cascade);
        e.Navigation(x => x.Rounds).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class RoundConfig : IEntityTypeConfiguration<Round>
{
    public void Configure(EntityTypeBuilder<Round> e)
    {
        e.ToTable("rounds");
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        e.HasIndex(x => new { x.GameId, x.Number }).IsUnique();

        e.OwnsMany(x => x.Allocations, a =>
        {
            a.ToTable("round_allocations");
            a.WithOwner().HasForeignKey("RoundId");
            a.Property<int>("Id");
            a.HasKey("Id");
            a.Property(p => p.Symbol).HasMaxLength(16).IsRequired();
        });
        e.Navigation(x => x.Allocations).UsePropertyAccessMode(PropertyAccessMode.Field);

        e.OwnsOne(x => x.Result, r =>
        {
            r.ToJson();
            r.OwnsMany(x => x.Assets);
        });
    }
}
