using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PaymentsBench.Application.Ports;
using PaymentsBench.Application.Reconciliation;
using PaymentsBench.Domain;
using PaymentsBench.Domain.Payments;
using PaymentsBench.Infrastructure.InMemory;
using PaymentsBench.Infrastructure.Partner;
using PaymentsBench.PartnerSimulator;

namespace PaymentsBench.Iso20022.Tests;

/// <summary>
/// The simulated partner hosted in process. Its camt.053 is rendered by code in the simulator that shares
/// nothing with <c>Camt053Reader</c>, so these tests read one implementation's output with the other's parser.
/// </summary>
public sealed class SimulatorStatementTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 10, 14);

    private readonly WebApplicationFactory<SimulatorEntryPoint> _simulator = new();

    public void Dispose() => _simulator.Dispose();

    [Fact]
    public async Task Reconciliation_is_clean_over_a_camt053_statement_served_by_the_simulator()
    {
        await SettleAtPartnerAsync("E2E-A", 100.00m);
        await SettleAtPartnerAsync("E2E-B", 250.50m);
        var payments = await SettledPaymentsAsync(("E2E-A", 100.00m), ("E2E-B", 250.50m));

        var report = await new ReconciliationService(payments, Source(StatementFormat.Camt053)).ReconcileAsync(Day, CancellationToken.None);

        Assert.True(report.IsClean);
        Assert.Equal(2, report.Matched);
    }

    [Fact]
    public async Task Reconciliation_names_each_kind_of_break_over_a_camt053_statement()
    {
        await SettleAtPartnerAsync("E2E-A", 100.00m);
        await SettleAtPartnerAsync("E2E-B", 250.50m);
        await SettleAtPartnerAsync("E2E-ONLY-AT-PARTNER", 5.00m);
        var payments = await SettledPaymentsAsync(("E2E-A", 100.00m), ("E2E-B", 999.00m), ("E2E-ONLY-HERE", 7.00m));

        var report = await new ReconciliationService(payments, Source(StatementFormat.Camt053)).ReconcileAsync(Day, CancellationToken.None);

        Assert.Equal(1, report.Matched);
        Assert.Contains(report.Breaks, b => b.Kind == BreakKind.AmountMismatch && b.EndToEndId == "E2E-B" && b.StatementAmount == 250.50m && b.LedgerAmount == 999.00m);
        Assert.Contains(report.Breaks, b => b.Kind == BreakKind.OnStatementOnly && b.EndToEndId == "E2E-ONLY-AT-PARTNER");
        Assert.Contains(report.Breaks, b => b.Kind == BreakKind.InLedgerOnly && b.EndToEndId == "E2E-ONLY-HERE");
        Assert.Equal(3, report.Breaks.Count);
    }

    [Fact]
    public async Task The_partner_statement_drift_fault_shows_as_amount_mismatches_in_camt053()
    {
        await SettleAtPartnerAsync("E2E-A", 100.00m);
        var payments = await SettledPaymentsAsync(("E2E-A", 100.00m));
        using var faults = await _simulator.CreateClient().PutAsJsonAsync("/faults", new { kind = "StatementDrift", driftAmount = 0.05m });
        faults.EnsureSuccessStatusCode();

        var report = await new ReconciliationService(payments, Source(StatementFormat.Camt053)).ReconcileAsync(Day, CancellationToken.None);

        var mismatch = Assert.Single(report.Breaks);
        Assert.Equal(BreakKind.AmountMismatch, mismatch.Kind);
        Assert.Equal(100.05m, mismatch.StatementAmount);
    }

    [Fact]
    public async Task The_json_and_the_camt053_statements_say_the_same_thing()
    {
        await SettleAtPartnerAsync("E2E-A", 100.00m);
        await SettleAtPartnerAsync("E2E-B", 250.50m);

        var json = await Source(StatementFormat.Json).GetAsync(Day, CancellationToken.None);
        var camt053 = await Source(StatementFormat.Camt053).GetAsync(Day, CancellationToken.None);

        Assert.Equal(json.Date, camt053.Date);
        Assert.Equal(json.Account, camt053.Account);
        Assert.Equal(json.Lines.OrderBy(l => l.EndToEndId), camt053.Lines.OrderBy(l => l.EndToEndId));
        Assert.Equal(2, camt053.Lines.Count);
    }

    [Fact]
    public async Task The_statement_source_asks_for_xml_only_when_configured_for_camt053()
    {
        var json = new CaptureAcceptHandler();
        var xml = new CaptureAcceptHandler();

        await new HttpStatementSource(_simulator.CreateDefaultClient(json), new PartnerStatementSettings(StatementFormat.Json)).GetAsync(Day, CancellationToken.None);
        await new HttpStatementSource(_simulator.CreateDefaultClient(xml), new PartnerStatementSettings(StatementFormat.Camt053)).GetAsync(Day, CancellationToken.None);

        Assert.Equal("application/json", json.Accept);
        Assert.Equal("application/xml", xml.Accept);
    }

    [Fact]
    public async Task The_simulator_serves_xml_only_to_a_caller_that_accepts_it()
    {
        using var client = _simulator.CreateClient();

        using var withXml = await client.SendAsync(StatementRequest("application/xml"));
        using var withJson = await client.SendAsync(StatementRequest("application/json"));
        using var withNothing = await client.SendAsync(StatementRequest(null));

        Assert.Equal("application/xml", withXml.Content.Headers.ContentType?.MediaType);
        Assert.Equal("application/json", withJson.Content.Headers.ContentType?.MediaType);
        Assert.Equal("application/json", withNothing.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(null, StatementFormat.Json)]
    [InlineData("", StatementFormat.Json)]
    [InlineData("json", StatementFormat.Json)]
    [InlineData("camt053", StatementFormat.Camt053)]
    [InlineData("CAMT053", StatementFormat.Camt053)]
    public void The_statement_format_defaults_to_json_and_reads_camt053(string? configured, StatementFormat expected)
    {
        Assert.Equal(expected, PartnerStatementSettings.From(configured).Format);
    }

    [Fact]
    public void An_unknown_statement_format_stops_start_up()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PartnerStatementSettings.From("mt940"));

        Assert.Contains("Partner:StatementFormat", ex.Message, StringComparison.Ordinal);
    }

    private HttpStatementSource Source(StatementFormat format) => new(_simulator.CreateClient(), new PartnerStatementSettings(format));

    private static HttpRequestMessage StatementRequest(string? accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/statements/{Day:yyyy-MM-dd}");
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        return request;
    }

    private async Task SettleAtPartnerAsync(string endToEndId, decimal amount)
    {
        using var client = _simulator.CreateClient();
        using var accepted = await client.PostAsJsonAsync("/transfers", new { endToEndId, amount, currency = "EUR", debtorAccount = "DE89370400440532013000", creditorAccount = "FR1420041010050500013M02606", scheme = "sepa" });
        accepted.EnsureSuccessStatusCode();
        using var settled = await client.PostAsync(new Uri($"/transfers/{endToEndId}/settle?valueDate={Day:yyyy-MM-dd}", UriKind.Relative), content: null);
        settled.EnsureSuccessStatusCode();
    }

    private static async Task<InMemoryPaymentRepository> SettledPaymentsAsync(params (string EndToEndId, decimal Amount)[] settled)
    {
        var repository = new InMemoryPaymentRepository();
        var now = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        foreach (var (endToEndId, amount) in settled)
        {
            var payment = Payment.Initiate(Guid.NewGuid(), $"key-{endToEndId}", "hash", endToEndId, new Money(amount, "EUR"), "DE89370400440532013000", "FR1420041010050500013M02606", "sepa", now);
            payment.MarkSubmitted(now);
            payment.MarkAccepted("PTR-1", now);
            payment.MarkSettled(Day, now);
            await repository.AddAsync(payment, CancellationToken.None);
        }

        return repository;
    }

    private sealed class CaptureAcceptHandler : DelegatingHandler
    {
        public string? Accept { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Accept = string.Join(",", request.Headers.Accept.Select(a => a.MediaType));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
