using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

namespace PaymentsBench.Api.Tests;

public sealed class IdentityTests : IDisposable
{
    private const string Authority = JwtTestHost.Authority;
    private readonly JwtTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Unauthenticated_initiation_is_401_when_an_authority_is_configured()
    {
        using var client = Jwt().CreateClient();

        var response = await PostPayment(client, "no-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_by_the_key_the_jwks_publishes_is_accepted()
    {
        using var client = Jwt().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(_host.SigningKey));

        var response = await PostPayment(client, "good-token");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_by_another_key_is_refused()
    {
        using var stranger = RSA.Create(2048);
        using var client = Jwt().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(stranger));

        var response = await PostPayment(client, "stranger");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("signature is invalid", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task A_token_for_another_audience_is_refused()
    {
        using var client = Jwt().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(_host.SigningKey, audience: "SomeOtherProduct"));

        var response = await PostPayment(client, "wrong-audience");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("audience", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task A_token_signed_with_a_non_rs256_algorithm_is_refused()
    {
        using var client = Jwt().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(_host.SigningKey, algorithm: SecurityAlgorithms.RsaSha512Signature));

        var response = await PostPayment(client, "rs512");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("signature is invalid", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Partner_notifications_need_a_token_too()
    {
        using var client = Jwt().CreateClient();

        var response = await client.PostAsJsonAsync("/partner/notifications", new { notificationId = "N-1", endToEndId = "E2E-X", kind = "Settled" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Health_endpoints_are_anonymous(string path)
    {
        using var client = Jwt().CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Without_an_authority_in_development_initiation_is_not_401()
    {
        using var client = Development().CreateClient();

        var response = await PostPayment(client, "dev-anonymous");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Openapi_is_anonymous_in_development_and_protected_elsewhere()
    {
        using var development = Development().CreateClient();
        using var deployed = Jwt().CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await development.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await deployed.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public void Without_an_authority_outside_development_start_up_fails_naming_the_setting()
    {
        var factory = Host("Production", authority: null);

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Auth__Authority", failure.ToString());
    }

    [Fact]
    public void An_authority_without_issuer_and_audience_fails_start_up_naming_them()
    {
        var factory = Host("Production", authority: Authority, issuer: null, audience: null);

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Auth__Issuer", failure.ToString());
    }

    private WebApplicationFactory<ApiEntryPoint> Jwt() => _host.Jwt();

    private WebApplicationFactory<ApiEntryPoint> Development() => _host.Development();

    private WebApplicationFactory<ApiEntryPoint> Host(string environment, string? authority, string? issuer = null, string? audience = null) =>
        _host.Host(environment, authority, issuer, audience);

    private static string Token(RSA key, string audience = JwtTestHost.Audience, string algorithm = SecurityAlgorithms.RsaSha256) =>
        JwtTestHost.Token(key, audience, algorithm);

    private static async Task<HttpResponseMessage> PostPayment(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new
            {
                endToEndId = $"E2E-{key}",
                amount = 100.00m,
                currency = "EUR",
                debtorAccount = "DE89370400440532013000",
                creditorAccount = "GB33BUKB20201555555555",
                scheme = "sepa",
            }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
}
