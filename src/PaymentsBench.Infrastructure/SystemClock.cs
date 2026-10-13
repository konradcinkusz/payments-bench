using PaymentsBench.Application.Ports;

namespace PaymentsBench.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
