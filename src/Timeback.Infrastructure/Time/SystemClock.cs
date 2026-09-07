using Timeback.Application.Abstractions;

namespace Timeback.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
