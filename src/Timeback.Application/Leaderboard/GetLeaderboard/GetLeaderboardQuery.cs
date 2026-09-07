namespace Timeback.Application.Leaderboard;

public sealed record GetLeaderboardQuery(int Count = 10);

public sealed record LeaderboardRowView(int Rank, string Nickname, int Score, string CreatedAtUtc);
