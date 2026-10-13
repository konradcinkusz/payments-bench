namespace PaymentsBench.Application.Ports;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
