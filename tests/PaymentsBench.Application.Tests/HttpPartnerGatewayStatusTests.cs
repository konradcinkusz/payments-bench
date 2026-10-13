using System.Net;
using Polly.CircuitBreaker;
using Polly.Timeout;
using PaymentsBench.Application.Ports;
using PaymentsBench.Infrastructure.Partner;

namespace PaymentsBench.Application.Tests;

/// <summary>The status lookup against a simulated partner whose transport misbehaves: never a throw, never a false "not found".</summary>
public sealed class HttpPartnerGatewayStatusTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    private static HttpPartnerGateway GatewayOver(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://partner.simulated") });

    public static TheoryData<Exception> TransportFailures => new()
    {
        new BrokenCircuitException(),
        new TimeoutRejectedException(),
        new HttpRequestException("connection refused"),
        new TaskCanceledException("timed out"),
    };

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task A_failed_lookup_is_unavailable_not_a_throw_and_not_not_found(Exception failure)
    {
        var result = await GatewayOver(new ThrowingHandler(failure)).GetStatusAsync("E2E-1", CancellationToken.None);

        Assert.Equal(PartnerStatus.Unavailable, result.Status);
        Assert.Equal(failure.GetType().Name, result.ReasonCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_5xx_answer_is_unavailable(HttpStatusCode code)
    {
        var gateway = GatewayOver(new StubHandler(_ => new HttpResponseMessage(code)));

        var result = await gateway.GetStatusAsync("E2E-1", CancellationToken.None);

        Assert.Equal(PartnerStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Only_a_404_is_not_found()
    {
        var gateway = GatewayOver(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Equal(PartnerStatus.NotFound, (await gateway.GetStatusAsync("E2E-1", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_still_cancels()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var gateway = GatewayOver(new ThrowingHandler(new TaskCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.GetStatusAsync("E2E-1", cancelled.Token));
    }
}
