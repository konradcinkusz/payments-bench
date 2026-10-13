using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Infrastructure.Outbox;

/// <summary>
/// One pass over the outbox: undispatched rows in <c>OccurredAt</c> order are handed to
/// <see cref="IEventPublisher"/>, and a row is marked dispatched only after its publish returned.
/// If the mark is lost (the save fails, the process dies) the row is published again with the
/// same id on the next batch: delivery is at-least-once and consumers dedupe on the message id.
/// </summary>
public sealed class OutboxBatchDispatcher(IServiceScopeFactory scopes, IEventPublisher publisher)
{
    public const int BatchSize = 100;

    /// <summary>Publishes up to <see cref="BatchSize"/> pending messages and returns how many were published and marked.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var pending = await db.OutboxMessages
            .Where(m => m.DispatchedAt == null)
            .OrderBy(m => m.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var dispatched = 0;
        foreach (var message in pending)
        {
            await publisher.PublishAsync(message.Id, message.Type, message.Payload, cancellationToken);
            message.DispatchedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            dispatched++;
        }

        return dispatched;
    }
}
