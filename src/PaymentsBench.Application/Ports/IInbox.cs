namespace PaymentsBench.Application.Ports;

/// <summary>
/// Partner notifications arrive at least once. The inbox records each notification id; the
/// second delivery is acknowledged and ignored.
/// </summary>
public interface IInbox
{
    /// <returns><c>true</c> if this is the first time the id was seen.</returns>
    Task<bool> TryRecordAsync(string notificationId, CancellationToken cancellationToken);
}
