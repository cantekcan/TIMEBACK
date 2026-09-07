using System.Security.Cryptography;
using System.Text;
using Timeback.Application.Abstractions;

namespace Timeback.Infrastructure.Security;

/// <summary>256-bit CSPRNG token, stored only as its SHA-256 hex hash.</summary>
public sealed class GameTokenFactory : IGameTokenFactory
{
    public GameToken Create()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexStringLower(raw);
        return new GameToken(token, HashOf(token));
    }

    public string HashOf(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
