namespace Timeback.Application.Abstractions;

public readonly record struct GameToken(string Value, string Hash);

/// <summary>Creates and hashes opaque game session tokens. Implemented with a CSPRNG in Infrastructure.</summary>
public interface IGameTokenFactory
{
    GameToken Create();
    string HashOf(string token);
}
