using System.Net.Http.Json;
using System.Text.Json;

namespace PaymentsBench.Api.Tests;

/// <summary>Each test runs against its own service instance. Helpers shared by the API tests: a client, a valid body, and problem-body readers.</summary>
public abstract class ApiTestBase : IDisposable
{
    private readonly ApiFactory _factory = new();

    // A service instance per test: in-memory state never leaks between tests, so totals can be exact.
    protected ApiTestBase() => Client = _factory.CreateClient();

    protected HttpClient Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    protected static object ValidBody(string? endToEndId = null, string scheme = "SEPA", string currency = "EUR", decimal amount = 125.50m) => new
    {
        endToEndId = endToEndId ?? $"E2E-{Guid.NewGuid():N}"[..20],
        amount,
        currency,
        debtorAccount = "DE89370400440532013000",
        creditorAccount = "FR1420041010050500013M02606",
        scheme,
    };

    protected async Task<HttpResponseMessage> InitiateAsync(object body, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        return await Client.SendAsync(request);
    }

    protected async Task<JsonElement> CreatePaymentAsync(string scheme = "SEPA")
    {
        var response = await InitiateAsync(ValidBody(scheme: scheme));
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    protected static void AssertProblem(HttpResponseMessage response, System.Net.HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    protected static async Task<string[]> InvalidMembersAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").EnumerateObject().Select(p => p.Name.ToLowerInvariant()).Order().ToArray();
    }
}
