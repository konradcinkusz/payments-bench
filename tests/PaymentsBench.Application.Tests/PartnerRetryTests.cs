using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentsBench.Application.Ports;
using PaymentsBench.Infrastructure;
using Polly.CircuitBreaker;

namespace PaymentsBench.Application.Tests;

/// <summary>
/// The partner client's retry rule (F-5), proved through the real resilience pipeline: a lookup
/// (<c>GET</c>) that throws is retried, a submit (<c>POST</c>) never is.
/// </summary>
public class PartnerRetryTests
{
    [Fact]
    public async Task A_status_lookup_that_throws_once_is_retried()
    {
        var handler = new ScriptedHandler(call => call == 1
            ? throw new HttpRequestException("connection refused")
            : Json(HttpStatusCode.OK, """{"status":"Accepted","partnerReference":"P-1"}"""));
        await using var provider = Build(handler);

        var result = await provider.GetRequiredService<IPartnerGateway>().GetStatusAsync("E2E-1", CancellationToken.None);

        Assert.Equal(PartnerStatus.Accepted, result.Status);
        Assert.Equal(["GET", "GET"], handler.Methods);
    }

    [Fact]
    public async Task A_status_lookup_that_hits_an_open_circuit_is_attempted_once()
    {
        var handler = new ScriptedHandler(_ => throw new BrokenCircuitException("circuit is open"));
        await using var provider = Build(handler);

        var result = await provider.GetRequiredService<IPartnerGateway>().GetStatusAsync("E2E-1", CancellationToken.None);

        Assert.Equal(PartnerStatus.Unavailable, result.Status);
        Assert.Equal(["GET"], handler.Methods);
    }

    [Fact]
    public async Task A_status_lookup_that_hits_an_isolated_circuit_is_attempted_once()
    {
        var handler = new ScriptedHandler(_ => throw new IsolatedCircuitException("circuit is isolated"));
        await using var provider = Build(handler);

        var result = await provider.GetRequiredService<IPartnerGateway>().GetStatusAsync("E2E-1", CancellationToken.None);

        Assert.Equal(PartnerStatus.Unavailable, result.Status);
        Assert.Equal(["GET"], handler.Methods);
    }

    [Fact]
    public async Task Once_failing_submits_have_opened_the_circuit_a_status_lookup_fails_fast()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        await using var provider = Build(handler);
        var gateway = provider.GetRequiredService<IPartnerGateway>();
        var transfer = new PartnerTransfer("E2E-1", 10m, "EUR", "DE89370400440532013000", "GB33BUKB20201555555555", "sepa");

        // Ten failures in the sampling window (the minimum throughput) at a 100 % failure ratio.
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(SubmitOutcome.Unknown, (await gateway.SubmitAsync(transfer, CancellationToken.None)).Outcome);
        }

        var reached = handler.Methods.Count;
        var clock = Stopwatch.StartNew();
        var result = await gateway.GetStatusAsync("E2E-1", CancellationToken.None);
        clock.Stop();

        Assert.Equal(PartnerStatus.Unavailable, result.Status);
        Assert.Equal(nameof(BrokenCircuitException), result.ReasonCode);
        Assert.Equal(reached, handler.Methods.Count); // the open circuit never reached the partner
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"an open circuit took {clock.Elapsed} to answer; it must fail fast, not wait out the retry backoff");
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("POST", false)]
    public async Task Only_a_lookup_that_threw_a_transport_error_is_retried(string method, bool retried)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "http://partner/x");
        var context = Polly.ResilienceContextPool.Shared.Get(CancellationToken.None);
        context.Properties.Set(new Polly.ResiliencePropertyKey<HttpRequestMessage?>("Resilience.Http.RequestMessage"), request);
        try
        {
            var args = new Polly.Retry.RetryPredicateArguments<HttpResponseMessage>(
                context, Polly.Outcome.FromException<HttpResponseMessage>(new HttpRequestException("reset")), 0);
            Assert.Equal(retried, await Infrastructure.Partner.PartnerResilience.ShouldRetry(args));
        }
        finally
        {
            Polly.ResilienceContextPool.Shared.Return(context);
        }
    }

    [Fact]
    public async Task A_submit_that_throws_is_never_retried_and_reports_an_unknown_outcome()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("connection reset"));
        await using var provider = Build(handler);

        var result = await provider.GetRequiredService<IPartnerGateway>().SubmitAsync(
            new PartnerTransfer("E2E-1", 10m, "EUR", "DE89370400440532013000", "GB33BUKB20201555555555", "sepa"),
            CancellationToken.None);

        Assert.Equal(SubmitOutcome.Unknown, result.Outcome);
        Assert.Equal(["POST"], handler.Methods);
    }

    [Fact]
    public async Task A_submit_answered_with_a_5xx_is_never_retried()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        await using var provider = Build(handler);

        var result = await provider.GetRequiredService<IPartnerGateway>().SubmitAsync(
            new PartnerTransfer("E2E-1", 10m, "EUR", "DE89370400440532013000", "GB33BUKB20201555555555", "sepa"),
            CancellationToken.None);

        Assert.Equal(SubmitOutcome.Unknown, result.Outcome);
        Assert.Equal(["POST"], handler.Methods);
    }

    [Fact]
    public void Partner_timeouts_default_to_the_values_the_service_always_had_and_can_be_configured()
    {
        var defaults = Infrastructure.Partner.PartnerTimeouts.From(new ConfigurationBuilder().Build());
        Assert.Equal(new Infrastructure.Partner.PartnerTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(15)), defaults);

        var configured = Infrastructure.Partner.PartnerTimeouts.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Partner:Timeout"] = "00:00:02",
            ["Partner:AttemptTimeout"] = "00:00:01",
            ["Partner:TotalTimeout"] = "00:00:06",
        }).Build());
        Assert.Equal(new Infrastructure.Partner.PartnerTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(6)), configured);
    }

    private static ServiceProvider Build(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Schemes:Directory"] = Path.Join(AppContext.BaseDirectory, "scenarios"),
            ["ConnectionStrings:payments"] = string.Empty,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPaymentsInfrastructure(configuration);
        services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method.Method);
            return Task.FromResult(respond(Methods.Count));
        }
    }
}
