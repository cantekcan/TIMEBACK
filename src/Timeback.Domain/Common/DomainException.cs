namespace Timeback.Domain.Common;

/// <summary>
/// Thrown when a domain invariant or business rule is violated. The Application/API layers
/// translate this into an RFC 7807 ProblemDetails 4xx response - it is never a 500.
/// </summary>
public sealed class DomainException(string message) : Exception(message);
