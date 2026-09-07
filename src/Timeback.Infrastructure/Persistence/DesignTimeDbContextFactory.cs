using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Timeback.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` at design time so migrations don't need the full API host.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TimebackDbContext>
{
    public TimebackDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TimebackDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=timeback;Username=timeback;Password=timeback",
                b => b.MigrationsAssembly(typeof(TimebackDbContext).Assembly.FullName))
            .Options;
        return new TimebackDbContext(options);
    }
}
