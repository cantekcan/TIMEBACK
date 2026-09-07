using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Scalar.AspNetCore;
using Serilog;
using Timeback.Api;
using Timeback.Application;
using Timeback.Infrastructure;
using Timeback.Infrastructure.MarketData.Ingestion;
using Timeback.Infrastructure.Seeding;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Default: generous per-IP budget for gameplay (each round is several calls).
    options.AddPolicy("default", Partition("default", permit: 120, windowSeconds: 60));
    // Creating games and writing to the leaderboard is much cheaper to abuse -> tighter.
    options.AddPolicy("mutation", Partition("mutation", permit: 15, windowSeconds: 60));

    static Func<HttpContext, RateLimitPartition<string>> Partition(string tag, int permit, int windowSeconds) =>
        http => RateLimitPartition.GetFixedWindowLimiter(
            $"{tag}:{http.Connection.RemoteIpAddress}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                QueueLimit = 0
            });
});

builder.Services.AddHealthChecks();

var app = builder.Build();

// CLI mode: `dotnet run -- ingest [--from yyyy-MM] [--to yyyy-MM]`
if (args.Length > 0 && args[0].Equals("ingest", StringComparison.OrdinalIgnoreCase))
{
    await RunIngestionAsync(app, args);
    return;
}

app.UseExceptionHandler();
app.UseSerilogRequestLogging();

if (app.Configuration.GetValue("Seed:OnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
}

app.MapOpenApi();
app.MapScalarApiReference();
app.UseCors();
app.UseRateLimiter();

app.MapControllers();
app.MapHealthChecks("/health").DisableRateLimiting();

await app.RunAsync();

static async Task RunIngestionAsync(WebApplication app, string[] args)
{
    static DateOnly Parse(string? s, DateOnly fallback) =>
        DateOnly.TryParse(s, out var d) ? d : fallback;

    var from = Parse(Arg(args, "--from"), new DateOnly(2015, 1, 1));
    var to = Parse(Arg(args, "--to"), DateOnly.FromDateTime(DateTime.UtcNow));

    using var scope = app.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedAsync();
    var ingestion = scope.ServiceProvider.GetRequiredService<MarketDataIngestionService>();
    var report = await ingestion.IngestAsync(from, to, CancellationToken.None);
    Console.WriteLine($"Ingestion complete: +{report.PricesInserted} prices, +{report.InflationInserted} CPI, " +
                      $"{report.PricesSkippedExisting} prices already present.");

    static string? Arg(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}

public partial class Program;
