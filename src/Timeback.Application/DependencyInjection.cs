using Microsoft.Extensions.DependencyInjection;
using Timeback.Application.Games;
using Timeback.Application.Leaderboard;

namespace Timeback.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<RoundDatePlanner>();
        services.AddScoped<GamePlayService>();

        services.AddScoped<StartGameHandler>();
        services.AddScoped<GetCurrentRoundHandler>();
        services.AddScoped<SubmitAllocationHandler>();
        services.AddScoped<GetGameResultHandler>();
        services.AddScoped<SaveLeaderboardEntryHandler>();
        services.AddScoped<GetLeaderboardHandler>();

        return services;
    }
}
