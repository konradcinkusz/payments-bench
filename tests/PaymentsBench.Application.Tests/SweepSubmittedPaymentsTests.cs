using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Payments;
using PaymentsBench.Infrastructure.Persistence;
using PaymentsBench.Infrastructure.Sweeping;

namespace PaymentsBench.Application.Tests;

/// <summary>The sweep over in-memory stores, a fixed clock and a scripted (simulated) partner.</summary>
public sealed class SweepSubmittedPaymentsTests
{
    private static readonly TimeSpan MinimumAge = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_payment_submitted_two_minutes_ago_is_resolved_to_accepted()
    {
        var sweep = new SweepHost();
        var id = await sweep.SubmitUnknownAsync("a");
        sweep.Host.Clock.UtcNow += TimeSpan.FromMinutes(2);
        sweep.Host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.Accepted, "P-1", null, null));

        var result = await sweep.Sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(PaymentStatus.Accepted, await sweep.StatusOfAsync(id));
        Assert.Equal(1, result.Outcomes[ResolutionOutcome.Accepted]);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task A_payment_submitted_ten_seconds_ago_is_untouched()
    {
        var sweep = new SweepHost();
        var id = await sweep.SubmitUnknownAsync("a");
        sweep.Host.Clock.UtcNow += TimeSpan.FromSeconds(10);

        var result = await sweep.Sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(PaymentStatus.Submitted, await sweep.StatusOfAsync(id));
        Assert.Equal(0, result.Examined);
        Assert.Empty(sweep.Host.Partner.StatusQueries);
        Assert.Equal(TimeSpan.FromSeconds(10), result.OldestSubmittedAge);
    }

    [Fact]
    public async Task A_partner_that_has_no_record_leads_to_exactly_one_resubmit_with_the_same_end_to_end_id()
    {
        var sweep = new SweepHost();
        var id = await sweep.SubmitUnknownAsync("a");
        sweep.Host.Clock.UtcNow += TimeSpan.FromMinutes(2);
        sweep.Host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.NotFound, null, null, null));
        sweep.Host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Accepted, "P-2", null));

        await sweep.Sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(PaymentStatus.Accepted, await sweep.StatusOfAsync(id));
        Assert.Equal(2, sweep.Host.Partner.Submits.Count);
        Assert.Equal(sweep.Host.Partner.Submits[0].EndToEndId, sweep.Host.Partner.Submits[1].EndToEndId);
    }

    [Fact]
    public async Task A_resolve_that_throws_does_not_stop_the_rest_of_the_batch_and_is_counted()
    {
        var sweep = new SweepHost();
        var first = await sweep.SubmitUnknownAsync("a");
        var broken = await sweep.SubmitUnknownAsync("b");
        var third = await sweep.SubmitUnknownAsync("c");
        sweep.Host.Clock.UtcNow += TimeSpan.FromMinutes(2);
        sweep.Host.Partner.ThrowOnStatusFor.Add("E2E-b");
        sweep.Host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.Accepted, "P-1", null, null));
        sweep.Host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.Accepted, "P-3", null, null));
        using var metrics = new MetricsProbe(sweep.MeterFactory);

        var result = await sweep.Sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(PaymentStatus.Accepted, await sweep.StatusOfAsync(first));
        Assert.Equal(PaymentStatus.Submitted, await sweep.StatusOfAsync(broken));
        Assert.Equal(PaymentStatus.Accepted, await sweep.StatusOfAsync(third));
        var failure = Assert.Single(result.Failures);
        Assert.Equal(broken, failure.PaymentId);
        Assert.Equal(2, metrics.Resolved("Accepted"));
        Assert.Equal(1, metrics.Resolved(SweepMetrics.FailedOutcome));
    }

    [Fact]
    public async Task A_concurrency_conflict_is_a_counted_failure_like_any_other()
    {
        var sweep = new SweepHost();
        await sweep.SubmitUnknownAsync("a");
        sweep.Host.Clock.UtcNow += TimeSpan.FromMinutes(2);
        var handler = new SweepSubmittedPaymentsHandler(sweep.Host.Payments, new ConflictingResolver(), sweep.Host.Clock);

        var result = await handler.HandleAsync(new SweepSubmittedPayments(MinimumAge, 10), CancellationToken.None);

        Assert.IsType<DbUpdateConcurrencyException>(Assert.Single(result.Failures).Exception);
    }

    [Fact]
    public async Task The_batch_size_is_honoured_and_the_oldest_payments_go_first()
    {
        var sweep = new SweepHost();
        foreach (var key in new[] { "a", "b", "c", "d", "e" })
        {
            await sweep.SubmitUnknownAsync(key);
            sweep.Host.Clock.UtcNow += TimeSpan.FromSeconds(10);
        }

        sweep.Host.Clock.UtcNow += TimeSpan.FromMinutes(5);
        sweep.Settings.BatchSize = 2;
        sweep.Host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.Accepted, "P-1", null, null));
        sweep.Host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.Accepted, "P-2", null, null));

        var result = await sweep.Sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, result.Examined);
        Assert.Equal(["E2E-a", "E2E-b"], sweep.Host.Partner.StatusQueries);
        var stillSubmitted = await sweep.Host.Payments.ListByStatusAsync(PaymentStatus.Submitted, CancellationToken.None);
        Assert.Equal(3, stillSubmitted.Count);
    }

    [Fact]
    public async Task The_metrics_carry_the_oldest_age_and_the_outcome_counts()
    {
        var sweep = new SweepHost();
        using var metrics = new MetricsProbe(sweep.MeterFactory);
        metrics.Observe();
        Assert.Equal(0, metrics.OldestAgeSeconds);

        var settled = await sweep.SubmitUnknownAsync("a");
        sweep.Host.Clock.UtcNow += TimeSpan.FromSeconds(1);
        var unknown = await sweep.SubmitUnknownAsync("b");
        sweep.Host.Clock.UtcNow += TimeSpan.FromMinutes(2);
        sweep.Host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.Settled, "P-1", null, new DateOnly(2026, 10, 14)));
        sweep.Host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.NotFound, null, null, null));
        sweep.Host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Unknown, null, "timeout"));

        await sweep.Sweeper.SweepOnceAsync(CancellationToken.None);
        metrics.Observe();

        Assert.Equal(PaymentStatus.Settled, await sweep.StatusOfAsync(settled));
        Assert.Equal(PaymentStatus.Submitted, await sweep.StatusOfAsync(unknown));
        Assert.Equal(1, metrics.Resolved("Settled"));
        Assert.Equal(1, metrics.Resolved("StillUnknown"));
        Assert.Equal(0, metrics.Resolved(SweepMetrics.FailedOutcome));

        // The payment that stays Submitted is the oldest one left: two minutes old, the sweep did not touch its UpdatedAt.
        Assert.Equal(120, metrics.OldestAgeSeconds);

        // Resolved by hand, the gauge falls to zero at the next sweep.
        sweep.Host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.Accepted, "P-2", null, null));
        await sweep.Sweeper.SweepOnceAsync(CancellationToken.None);
        metrics.Observe();
        Assert.Equal(0, metrics.OldestAgeSeconds);
    }

    [Fact]
    public async Task A_sweep_is_an_activity_on_the_payments_bench_source()
    {
        var sweep = new SweepHost();
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "PaymentsBench",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await sweep.Sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Contains(stopped, a => a.OperationName == "payments.sweep");
    }

    [Fact]
    public async Task The_hosted_service_sweeps_on_its_own_once_the_gate_is_open()
    {
        var sweep = new SweepHost();
        var id = await sweep.SubmitUnknownAsync("a");
        sweep.Host.Clock.UtcNow += TimeSpan.FromMinutes(2);
        sweep.Host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.Accepted, "P-1", null, null));
        sweep.Settings.Interval = TimeSpan.FromMilliseconds(20);

        await sweep.Sweeper.StartAsync(CancellationToken.None);
        try
        {
            await Eventually(async () => await sweep.StatusOfAsync(id) == PaymentStatus.Accepted);
        }
        finally
        {
            await sweep.Sweeper.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_disabled_sweeper_starts_nothing()
    {
        var sweep = new SweepHost();
        await sweep.SubmitUnknownAsync("a");
        sweep.Host.Clock.UtcNow += TimeSpan.FromMinutes(2);
        sweep.Settings.Enabled = false;

        await sweep.Sweeper.StartAsync(CancellationToken.None);
        await sweep.Sweeper.ExecuteTask!;
        await sweep.Sweeper.StopAsync(CancellationToken.None);

        Assert.Empty(sweep.Host.Partner.StatusQueries);
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ConflictingResolver : ISubmittedPaymentResolver
    {
        public Task<ResolutionOutcome> ResolveAsync(Guid paymentId, CancellationToken cancellationToken) =>
            throw new DbUpdateConcurrencyException("Someone resolved it by hand.");
    }

    /// <summary>The sweeper wired the way the API wires it, over the test host's in-memory stores.</summary>
    private sealed class SweepHost
    {
        public SweepHost()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IClock>(Host.Clock);
            services.AddSingleton<IPaymentRepository>(Host.Payments);
            services.AddSingleton<ILedger>(Host.Ledger);
            services.AddSingleton<IOutbox>(Host.Outbox);
            services.AddSingleton<IUnitOfWork>(Host.UnitOfWork);
            services.AddSingleton<IPartnerGateway>(Host.Partner);
            services.AddSingleton(MigrationGate.Completed());
            services.AddScoped<ResolveSubmittedPaymentHandler>();
            services.AddLogging();
            services.AddSubmittedPaymentSweeper(new ConfigurationBuilder().Build());
            services.AddSingleton<IOptions<SweeperOptions>>(Options.Create(Settings));
            Services = services.BuildServiceProvider();
            Sweeper = Services.GetServices<IHostedService>().OfType<SubmittedPaymentSweeper>().Single();
        }

        public TestHost Host { get; } = new();

        public SweeperOptions Settings { get; } = new() { MinimumAge = MinimumAge };

        public ServiceProvider Services { get; }

        public SubmittedPaymentSweeper Sweeper { get; }

        public IMeterFactory MeterFactory => Services.GetRequiredService<IMeterFactory>();

        public async Task<Guid> SubmitUnknownAsync(string suffix)
        {
            var payment = await Host.Initiate.HandleAsync(TestHost.Command($"key-{suffix}", $"E2E-{suffix}"), CancellationToken.None);
            Host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Unknown, null, "timeout"));
            await Host.Submit.HandleAsync(new SubmitPayment(payment.Id), CancellationToken.None);
            return payment.Id;
        }

        public async Task<PaymentStatus> StatusOfAsync(Guid id) =>
            (await Host.Payments.GetAsync(id, CancellationToken.None))!.Status;
    }

    /// <summary>Reads the sweep instruments with a <see cref="MeterListener"/>, only from meters this test's factory created.</summary>
    private sealed class MetricsProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _resolved = [];
        private double _oldest = double.NaN;

        public MetricsProbe(IMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Meter.Name == "PaymentsBench")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                Assert.Equal("payments.sweep.resolved", instrument.Name);
                var outcome = (string)tags.ToArray().Single(t => t.Key == "outcome").Value!;
                _resolved[outcome] = _resolved.GetValueOrDefault(outcome) + value;
            });
            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            {
                Assert.Equal("payments.submitted.oldest_age_seconds", instrument.Name);
                Assert.Equal("s", instrument.Unit);
                _oldest = value;
            });
            _listener.Start();
        }

        public double OldestAgeSeconds => _oldest;

        public long Resolved(string outcome) => _resolved.GetValueOrDefault(outcome);

        public void Observe() => _listener.RecordObservableInstruments();

        public void Dispose() => _listener.Dispose();
    }
}
