using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentsBench.Application.Ports;
using PaymentsBench.Infrastructure;

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
