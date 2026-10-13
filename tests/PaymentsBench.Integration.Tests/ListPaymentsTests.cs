using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Payments;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Integration.Tests;

/// <summary>
/// <c>EfPaymentRepository.ListAsync</c> against real PostgreSQL. The class shares one database, so
/// each test seeds under its own scheme name and filters by it, which keeps totals exact.
/// </summary>
public sealed class ListPaymentsTests(MigratedPostgresFixture database) : IClassFixture<MigratedPostgresFixture>
{
    private static readonly DateTimeOffset Base = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Seeds one payment per (status, minute offset) under a fresh scheme name and returns the name and the payments.</summary>
    private async Task<(string Scheme, List<Payment> Payments)> SeedAsync(params (PaymentStatus Status, int Minute)[] rows)
    {
        var scheme = Samples.Unique("list");
        var payments = rows.Select(r => Samples.PaymentIn(r.Status, scheme: scheme, createdAt: Base.AddMinutes(r.Minute))).ToList();
        await using var write = database.CreateContext();
        var repository = new EfPaymentRepository(write);
        foreach (var payment in payments)
        {
            await repository.AddAsync(payment, CancellationToken.None);
        }

        await new EfUnitOfWork(write).CommitAsync(CancellationToken.None);
        return (scheme, payments);
    }

    private async Task<PaymentPage> ListAsync(PaymentQuery query)
    {
        await using var read = database.CreateContext();
        return await new EfPaymentRepository(read).ListAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task Total_and_counts_describe_the_filtered_set_and_not_the_page()
    {
        var (scheme, _) = await SeedAsync(
            (PaymentStatus.Initiated, 0), (PaymentStatus.Initiated, 1), (PaymentStatus.Accepted, 2), (PaymentStatus.Settled, 3), (PaymentStatus.Rejected, 4));

        var page = await ListAsync(PaymentQuery.Create(scheme: scheme, limit: 2));

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(5, page.Total);
        Assert.Equal(2, page.CountsByStatus[PaymentStatus.Initiated]);
        Assert.Equal(1, page.CountsByStatus[PaymentStatus.Accepted]);
        Assert.Equal(1, page.CountsByStatus[PaymentStatus.Settled]);
        Assert.Equal(1, page.CountsByStatus[PaymentStatus.Rejected]);
        Assert.False(page.CountsByStatus.ContainsKey(PaymentStatus.Returned));
    }

    [Fact]
    public async Task Items_are_newest_first_and_pages_partition_the_set()
    {
        var (scheme, payments) = await SeedAsync(
            (PaymentStatus.Initiated, 0), (PaymentStatus.Initiated, 1), (PaymentStatus.Initiated, 2), (PaymentStatus.Initiated, 3), (PaymentStatus.Initiated, 4));
        var expected = payments.OrderByDescending(p => p.CreatedAt).Select(p => p.Id).ToList();

        var first = await ListAsync(PaymentQuery.Create(scheme: scheme, page: 1, limit: 2));
        var second = await ListAsync(PaymentQuery.Create(scheme: scheme, page: 2, limit: 2));
        var third = await ListAsync(PaymentQuery.Create(scheme: scheme, page: 3, limit: 2));

        Assert.Equal(expected, first.Items.Concat(second.Items).Concat(third.Items).Select(p => p.Id));
        Assert.Single(third.Items);
    }

    [Fact]
    public async Task Equal_timestamps_are_ordered_by_id()
    {
        var (scheme, payments) = await SeedAsync((PaymentStatus.Initiated, 0), (PaymentStatus.Initiated, 0), (PaymentStatus.Initiated, 0));

        var page = await ListAsync(PaymentQuery.Create(scheme: scheme));

        Assert.Equal(payments.Select(p => p.Id).Order(), page.Items.Select(p => p.Id));
    }

    [Fact]
    public async Task Filters_by_status_scheme_end_to_end_id_and_created_range()
    {
        var (scheme, payments) = await SeedAsync(
            (PaymentStatus.Initiated, 0), (PaymentStatus.Accepted, 10), (PaymentStatus.Accepted, 20), (PaymentStatus.Settled, 30));

        var accepted = await ListAsync(PaymentQuery.Create(status: PaymentStatus.Accepted, scheme: scheme));
        Assert.Equal(2, accepted.Total);
        Assert.Equal(2, accepted.CountsByStatus[PaymentStatus.Accepted]);
        Assert.False(accepted.CountsByStatus.ContainsKey(PaymentStatus.Settled));

        var byEndToEnd = await ListAsync(PaymentQuery.Create(endToEndId: payments[3].EndToEndId));
        Assert.Equal(payments[3].Id, Assert.Single(byEndToEnd.Items).Id);

        var inRange = await ListAsync(PaymentQuery.Create(scheme: scheme, createdFrom: Base.AddMinutes(10), createdTo: Base.AddMinutes(20)));
        Assert.Equal(2, inRange.Total);

        Assert.Equal(0, (await ListAsync(PaymentQuery.Create(scheme: scheme, createdFrom: Base.AddMinutes(31)))).Total);
        Assert.Equal(0, (await ListAsync(PaymentQuery.Create(scheme: Samples.Unique("no-such-scheme")))).Total);
    }

    [Fact]
    public async Task Paging_is_clamped_by_the_query_and_a_page_past_the_end_is_empty()
    {
        var (scheme, _) = await SeedAsync((PaymentStatus.Initiated, 0), (PaymentStatus.Initiated, 1));

        var clamped = PaymentQuery.Create(scheme: scheme, page: 0, limit: 1_000_000);
        Assert.Equal(1, clamped.Page);
        Assert.Equal(100, clamped.Limit);
        Assert.Equal(2, (await ListAsync(clamped)).Items.Count);

        var past = await ListAsync(PaymentQuery.Create(scheme: scheme, page: 9));
        Assert.Empty(past.Items);
        Assert.Equal(2, past.Total);
    }
}
