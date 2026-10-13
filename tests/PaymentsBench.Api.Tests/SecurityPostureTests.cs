using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Api.Tests;

/// <summary>
/// Properties docs/security/SECURITY-REVIEW.md reports as positive findings. They are tests so that
/// the next change that loses one is a red build, not a line in the next review.
/// </summary>
public sealed class SecurityPostureTests : IDisposable
{
    private const string Secret = "Host=db.internal;Password=hunter2-should-never-be-shown";

    private readonly JwtTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private sealed class ExplodingRepository : IPaymentRepository
    {
        public Task<Payment?> GetAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException(Secret);

        public Task<Payment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) => throw new InvalidOperationException(Secret);

        public Task<Payment?> FindByEndToEndIdAsync(string endToEndId, CancellationToken cancellationToken) => throw new InvalidOperationException(Secret);

        public Task<IReadOnlyList<Payment>> ListByStatusAsync(PaymentStatus status, CancellationToken cancellationToken) => throw new InvalidOperationException(Secret);

        public Task<SubmittedPaymentWindow> ListSubmittedBeforeAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken) => throw new InvalidOperationException(Secret);

        public Task<PaymentPage> ListAsync(PaymentQuery query, CancellationToken cancellationToken) => throw new InvalidOperationException(Secret);

        public Task AddAsync(Payment payment, CancellationToken cancellationToken) => throw new InvalidOperationException(Secret);

        public Task UpdateAsync(Payment payment, CancellationToken cancellationToken) => throw new InvalidOperationException(Secret);
    }

    private HttpClient Authenticated(string environment, Action<IWebHostBuilder>? configure = null)
    {
        var factory = environment == "Development"
            ? _host.Development()
            : _host.Jwt(configure);
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services => services.AddScoped<IPaymentRepository, ExplodingRepository>());
            configure?.Invoke(builder);
        }).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token());
        return client;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task An_unexpected_exception_is_a_500_problem_with_fixed_text_in_every_environment(string environment)
    {
        using var client = Authenticated(environment);

        var response = await client.GetAsync("/payments/00000000-0000-0000-0000-000000000001");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("hunter2", body, StringComparison.Ordinal);
        Assert.DoesNotContain("db.internal", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.Contains("An unexpected error occurred.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_400_with_fixed_text_not_the_parsers_message()
    {
        using var client = _host.Jwt().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments") { Content = new StringContent("{ not json", Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", "bad-body");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("JsonException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("LineNumber", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_health_endpoint_says_Healthy_and_nothing_about_what_it_checked()
    {
        using var client = _host.Jwt().CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/metrics")]
    [InlineData("/payments")]
    [InlineData("/ledger/balances/customer-funds/EUR")]
    [InlineData("/swagger")]
    [InlineData("/no-such-route")]
    public async Task A_deployed_host_answers_nothing_but_health_without_a_token(string path)
    {
        using var client = _host.Jwt().CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Responses_do_not_name_the_server_software()
    {
        using var client = _host.Jwt().CreateClient();

        var response = await client.GetAsync("/health");

        Assert.False(response.Headers.Contains("X-Powered-By"));
    }
}
