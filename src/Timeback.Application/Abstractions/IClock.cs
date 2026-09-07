namespace Timeback.Application.Abstractions;

/// <summary>UTC clock abstraction so round deadlines are testable and never depend on the client.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
