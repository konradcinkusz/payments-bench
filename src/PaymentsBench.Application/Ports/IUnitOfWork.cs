namespace PaymentsBench.Application.Ports;

/// <summary>Commits the payment, its ledger entries and its outbox rows together, or none of them.</summary>
public interface IUnitOfWork
{
    Task CommitAsync(CancellationToken cancellationToken);
}
