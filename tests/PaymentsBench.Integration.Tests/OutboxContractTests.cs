using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain;
using PaymentsBench.Domain.Payments;
using PaymentsBench.Infrastructure.Outbox;
using PaymentsBench.Infrastructure.Persistence;
using PaymentsBench.Infrastructure.Schemes;

namespace PaymentsBench.Integration.Tests;

/// <summary>
/// The outbox contract against PostgreSQL, with the in-process channel standing in for a broker:
/// at-least-once from the dispatcher, exactly-once effect from a consumer that dedupes on the
/// message id. Each test gets its own database, so the dispatcher sees only its own rows.
/// </summary>
public sealed class OutboxContractTests : IAsyncLifetime, IDisposable
{
    private static readonly int[] MinutesOutOfOrder = [5, 1, 4, 2, 3];

    private static readonly ISchemeCatalog Schemes = SchemeCatalogLoader.Load(Path.Join(AppContext.BaseDirectory, "scenarios"));

    private readonly MigratedPostgresFixture _database = new();
    private readonly FailNextSave _failNextSave = new();
    private readonly CollectingHandler _handler = new();
    private ServiceProvider _services = null!;
    private ChannelEventPublisher _publisher = null!;
    private OutboxBatchDispatcher _dispatcher = null!;
    private DedupingConsumer _consumer = null!;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _services = new ServiceCollection()
            .AddDbContext<PaymentsDbContext>(options => options.UseNpgsql(_database.ConnectionString).AddInterceptors(_failNextSave))
            .BuildServiceProvider();
        _publisher = new ChannelEventPublisher(capacity: 16);
        _dispatcher = new OutboxBatchDispatcher(_services.GetRequiredService<IServiceScopeFactory>(), _publisher);
        _consumer = new DedupingConsumer(_publisher.Reader, new InMemoryProcessedMessageStore(), _handler, NullLogger<DedupingConsumer>.Instance);
    }

    public void Dispose() => _consumer?.Dispose();

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task An_event_enqueued_with_a_payment_change_is_delivered_once_with_the_row_id_and_then_marked_dispatched()
    {
        var created = await Initiate("delivered");
        var row = await SingleInitiatedRowAsync();
        Assert.Null(row.DispatchedAt);

        Assert.Equal(1, await _dispatcher.DispatchBatchAsync(CancellationToken.None));
        await _consumer.DrainAsync(CancellationToken.None);

        var delivered = Assert.Single(_handler.Received);
        Assert.Equal(row.Id, delivered.MessageId);
        Assert.Equal(nameof(PaymentInitiated), delivered.Type);
        Assert.Contains(created.Id.ToString(), delivered.Payload, StringComparison.Ordinal);
        Assert.Equal(1, _consumer.Applied);
        Assert.Equal(0, _consumer.DuplicatesDropped);

        await using var read = _database.CreateContext();
        Assert.NotNull((await read.OutboxMessages.SingleAsync(m => m.Id == row.Id)).DispatchedAt);

        Assert.Equal(0, await _dispatcher.DispatchBatchAsync(CancellationToken.None));
        await _consumer.DrainAsync(CancellationToken.None);
        Assert.Single(_handler.Received);
    }

    [Fact]
    public async Task A_publish_whose_dispatched_mark_is_lost_is_republished_with_the_same_id_and_applied_once()
    {
        await Initiate("crash");
        var row = await SingleInitiatedRowAsync();

        _failNextSave.Arm();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _dispatcher.DispatchBatchAsync(CancellationToken.None));

        await using (var read = _database.CreateContext())
        {
            Assert.Null((await read.OutboxMessages.SingleAsync(m => m.Id == row.Id)).DispatchedAt);
        }

        Assert.Equal(1, await _dispatcher.DispatchBatchAsync(CancellationToken.None));
        await _consumer.DrainAsync(CancellationToken.None);

        Assert.Equal(2, _publisher.PublishedCount);
        Assert.Equal(row.Id, Assert.Single(_handler.Received).MessageId);
        Assert.Equal(1, _consumer.Applied);
        Assert.Equal(1, _consumer.DuplicatesDropped);

        await using var after = _database.CreateContext();
        Assert.NotNull((await after.OutboxMessages.SingleAsync(m => m.Id == row.Id)).DispatchedAt);
    }

    [Fact]
    public async Task Messages_are_published_in_occurred_at_order()
    {
        // Written out of order on purpose: insertion order must not decide.
        var times = MinutesOutOfOrder.Select(minutes => Samples.Start.AddMinutes(minutes)).ToArray();
        await using (var write = _database.CreateContext())
        {
            var outbox = new EfOutbox(write);
            foreach (var at in times)
            {
                await outbox.EnqueueAsync(new PaymentSubmitted(Guid.NewGuid(), at), CancellationToken.None);
            }

            await new EfUnitOfWork(write).CommitAsync(CancellationToken.None);
        }

        await _dispatcher.DispatchBatchAsync(CancellationToken.None);
        await _consumer.DrainAsync(CancellationToken.None);

        Assert.Equal(5, _handler.Received.Count);
        var order = _handler.Received.Select(m => PayloadTime(m.Payload)).ToArray();
        Assert.Equal(times.OrderBy(t => t).ToArray(), order);
    }

    [Fact]
    public async Task Nothing_is_published_for_a_unit_of_work_that_failed_to_commit()
    {
        var key = Samples.Unique("dup");
        await using (var first = _database.CreateContext())
        {
            await new EfPaymentRepository(first).AddAsync(NewPayment(key), CancellationToken.None);
            await new EfUnitOfWork(first).CommitAsync(CancellationToken.None);
        }

        // Same idempotency key: the unique index refuses the payment, so the event beside it must go too.
        await using (var failing = _database.CreateContext())
        {
            var payment = NewPayment(key);
            await new EfPaymentRepository(failing).AddAsync(payment, CancellationToken.None);
            await new EfOutbox(failing).EnqueueAsync(new PaymentInitiated(payment.Id, Samples.Start, new Money(1m, "EUR"), "sepa"), CancellationToken.None);
            await Assert.ThrowsAsync<DbUpdateException>(() => new EfUnitOfWork(failing).CommitAsync(CancellationToken.None));
        }

        // And a unit of work abandoned before commit.
        await using (var abandoned = _database.CreateContext())
        {
            await new EfOutbox(abandoned).EnqueueAsync(new PaymentSubmitted(Guid.NewGuid(), Samples.Start), CancellationToken.None);
        }

        Assert.Equal(0, await _dispatcher.DispatchBatchAsync(CancellationToken.None));
        await _consumer.DrainAsync(CancellationToken.None);

        Assert.Empty(_handler.Received);
        Assert.Equal(0, _publisher.PublishedCount);
        await using var read = _database.CreateContext();
        Assert.Equal(0, await read.OutboxMessages.CountAsync());
    }

    private async Task<OutboxMessageRow> SingleInitiatedRowAsync()
    {
        await using var read = _database.CreateContext();
        return await read.OutboxMessages.SingleAsync(m => m.Type == nameof(PaymentInitiated));
    }

    private static DateTimeOffset PayloadTime(string payload) =>
        System.Text.Json.JsonDocument.Parse(payload).RootElement.GetProperty("occurredAt").GetDateTimeOffset();

    private static Payment NewPayment(string key) =>
        Payment.Initiate(Guid.NewGuid(), key, "hash-" + Guid.NewGuid().ToString("N"), Samples.Unique("E2E"),
            new Money(10m, "EUR"), "DE89370400440532013000", "GB33BUKB20201555555555", "sepa", Samples.Start);

    private async Task<Payment> Initiate(string name)
    {
        await using var db = _database.CreateContext();
        var handler = new InitiatePaymentHandler(new EfPaymentRepository(db), new EfLedger(db), new EfOutbox(db), new EfUnitOfWork(db), new FixedClock(Samples.Start), Schemes);
        return await handler.HandleAsync(
            new InitiatePayment(Samples.Unique(name), Samples.Unique("E2E"), 42m, "EUR", "DE89370400440532013000", "GB33BUKB20201555555555", "sepa"),
            CancellationToken.None);
    }

    private sealed class CollectingHandler : IOutboxMessageHandler
    {
        public List<OutboxEnvelope> Received { get; } = [];

        public Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken)
        {
            Received.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>Makes the next save fail once, after the publish and before the dispatched mark is stored.</summary>
    private sealed class FailNextSave : SaveChangesInterceptor
    {
        private int _armed;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Interlocked.Exchange(ref _armed, 0) == 1
                ? throw new InvalidOperationException("Injected failure: the dispatched mark was not saved.")
                : ValueTask.FromResult(result);
    }
}
