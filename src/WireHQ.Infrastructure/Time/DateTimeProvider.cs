using WireHQ.Application.Abstractions;

namespace WireHQ.Infrastructure.Time;

public sealed class DateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
