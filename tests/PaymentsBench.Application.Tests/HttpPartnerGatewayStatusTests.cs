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

    private static HttpClient ClientOver(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://partner.simulated") };

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
        using var client = ClientOver(new ThrowingHandler(failure));

        var result = await new HttpPartnerGateway(client).GetStatusAsync("E2E-1", CancellationToken.None);

        Assert.Equal(PartnerStatus.Unavailable, result.Status);
        Assert.Equal(failure.GetType().Name, result.ReasonCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_5xx_answer_is_unavailable(HttpStatusCode code)
    {
        using var client = ClientOver(new StubHandler(_ => new HttpResponseMessage(code)));

        var result = await new HttpPartnerGateway(client).GetStatusAsync("E2E-1", CancellationToken.None);

        Assert.Equal(PartnerStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Only_a_404_is_not_found()
    {
        using var client = ClientOver(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Equal(PartnerStatus.NotFound, (await new HttpPartnerGateway(client).GetStatusAsync("E2E-1", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_still_cancels()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var client = ClientOver(new ThrowingHandler(new TaskCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HttpPartnerGateway(client).GetStatusAsync("E2E-1", cancelled.Token));
    }
}
