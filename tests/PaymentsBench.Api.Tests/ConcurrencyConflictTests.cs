using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaymentsBench.Application.Ports;

namespace PaymentsBench.Api.Tests;

/// <summary>A lost optimistic-concurrency race reaches the client as 409 with a problem body, never as a 500.</summary>
public sealed class ConcurrencyConflictTests : IDisposable
{
    private readonly ConflictingFactory _factory = new();

    [Fact]
    public async Task A_commit_that_loses_a_race_is_a_409_problem_that_says_to_re_read()
    {
        using var client = _factory.CreateClient();
        using var created = await Initiate(client);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        _factory.Conflict = true;

        using var response = await client.PostAsync($"/payments/{id}/submit", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Contains("concurrently", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("Re-read", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<HttpResponseMessage> Initiate(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new
            {
                endToEndId = $"E2E-{Guid.NewGuid():N}"[..20],
                amount = 10m,
                currency = "EUR",
                debtorAccount = "DE89370400440532013000",
                creditorAccount = "FR1420041010050500013M02606",
                scheme = "sepa",
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    /// <summary>The service's in-memory unit of work, replaced by one that loses the race once told to.</summary>
    private sealed class ConflictingFactory : ApiFactory
    {
        public volatile bool Conflict;

        protected override void ConfigureAdditional(IWebHostBuilder builder) =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUnitOfWork>();
                services.AddSingleton<IUnitOfWork>(new RacingUnitOfWork(this));
            });
    }

    private sealed class RacingUnitOfWork(ConflictingFactory factory) : IUnitOfWork
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            factory.Conflict ? throw new ConcurrencyConflictException() : Task.CompletedTask;
    }
}
