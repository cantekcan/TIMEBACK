using System.Text.RegularExpressions;
using Timeback.Domain.Common;

namespace Timeback.Domain.Leaderboard;

/// <summary>A player-chosen display name: 3-16 chars, letters/digits/_-. only, trimmed, case preserved.</summary>
public readonly record struct Nickname
{
    public string Value { get; }
    private Nickname(string value) => Value = value;

    private static readonly Regex Allowed = new(@"^[A-Za-z0-9_\-\.]{3,16}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static Nickname Create(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (!Allowed.IsMatch(trimmed))
            throw new DomainException("Nickname must be 3-16 characters: letters, digits, '_', '-' or '.'.");
        return new Nickname(trimmed);
    }

    public override string ToString() => Value;
}

/// <summary>
/// A leaderboard record. The score is copied from the (server-computed) completed game - the client
/// only ever supplies a nickname. One entry per game enforces "no re-submitting the same result".
/// </summary>
public sealed class LeaderboardEntry : AggregateRoot
{
    public Guid GameId { get; private set; }
    public string Nickname { get; private set; } = null!;
    public int Score { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private LeaderboardEntry() { }

    public LeaderboardEntry(Guid gameId, Nickname nickname, int score, DateTime nowUtc)
    {
        if (score < 0) throw new DomainException("Score cannot be negative.");
        GameId = gameId;
        Nickname = nickname.Value;
        Score = score;
        CreatedAtUtc = nowUtc;
    }
}
