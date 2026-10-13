using Microsoft.EntityFrameworkCore;
using PaymentsBench.Domain;
using PaymentsBench.Domain.Payments;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Integration.Tests;

public sealed class OutboxAndInboxTests(MigratedPostgresFixture database) : IClassFixture<MigratedPostgresFixture>
{
    [Fact]
    public async Task An_enqueued_event_is_an_undispatched_row_with_type_payload_and_time()
    {
        var paymentId = Guid.NewGuid();
        var occurredAt = Samples.Start.AddMinutes(3);
        await using (var write = database.CreateContext())
        {
            await new EfOutbox(write).EnqueueAsync(new PaymentInitiated(paymentId, occurredAt, new Money(10m, "EUR"), "sepa"), CancellationToken.None);
            await new EfUnitOfWork(write).CommitAsync(CancellationToken.None);
        }

        await using var read = database.CreateContext();
        var rows = await read.OutboxMessages.Where(m => m.Payload.Contains(paymentId.ToString())).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(nameof(PaymentInitiated), row.Type);
        Assert.Equal(occurredAt, row.OccurredAt);
        Assert.Null(row.DispatchedAt);
        Assert.Contains("\"scheme\":\"sepa\"", row.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_record_of_an_id_is_accepted_and_committed_and_the_second_is_refused()
    {
        var id = Samples.Unique("notification");
        await using (var first = database.CreateContext())
        {
            Assert.True(await new EfInbox(first).TryRecordAsync(id, CancellationToken.None));
            await new EfUnitOfWork(first).CommitAsync(CancellationToken.None);
        }

        await using (var second = database.CreateContext())
        {
            Assert.False(await new EfInbox(second).TryRecordAsync(id, CancellationToken.None));
            await new EfUnitOfWork(second).CommitAsync(CancellationToken.None);
        }

        await using var read = database.CreateContext();
        Assert.Equal(1, await read.InboxMessages.CountAsync(m => m.NotificationId == id));
    }
}
