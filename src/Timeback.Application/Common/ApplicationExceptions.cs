namespace Timeback.Application.Common;

/// <summary>Requested resource does not exist (or the caller isn't authorized to see it) -> HTTP 404.</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>Request is well-formed but violates an application rule -> HTTP 409.</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>A concurrent write lost the optimistic-concurrency race -> HTTP 409, safe to retry.</summary>
public sealed class ConcurrencyConflictException(string message)
    : Exception(message);
