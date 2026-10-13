using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;

namespace PaymentsBench.Api.Tests;

/// <summary>
/// "Authenticated" is not enough for three groups of endpoints (SEC-02, SEC-04): the partner webhook
/// moves money between ledger accounts, and the ledger and reconciliation expose every customer's
/// movements. They need a grant on the token. Everything else stays open to any authenticated caller
/// until an ownership model exists (SEC-01).
/// </summary>
public sealed class AuthorizationPolicyTests : IDisposable
{
    private const string PartnerGrant = "payments.partner.notify";
    private const string OperationsGrant = "payments.operate";

    private readonly JwtTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private HttpClient ClientWith(params Claim[] claims) => ClientWith(null, claims);

    private HttpClient ClientWith(Action<IWebHostBuilder>? configure, params Claim[] claims)
    {
        var client = _host.Jwt(configure).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token("caller", claims));
        return client;
    }

    private static Task<HttpResponseMessage> Notify(HttpClient client) =>
        client.PostAsJsonAsync("/partner/notifications", new { notificationId = $"N-{Guid.NewGuid():N}", endToEndId = "E2E-UNKNOWN", kind = "Settled" });

    [Fact]
    public async Task A_token_without_the_grant_cannot_post_a_partner_notification()
    {
        using var client = ClientWith();

        Assert.Equal(HttpStatusCode.Forbidden, (await Notify(client)).StatusCode);
    }

    [Fact]
    public async Task No_token_is_still_401_not_403()
    {
        using var client = _host.Jwt().CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Notify(client)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/ledger/balances/customer-funds/EUR")).StatusCode);
    }

    [Theory]
    [InlineData("role", PartnerGrant)]
    [InlineData("roles", PartnerGrant)]
    [InlineData("scope", PartnerGrant)]
    [InlineData("scope", "openid " + PartnerGrant + " profile")]
    [InlineData("scp", "openid " + PartnerGrant)]
    public async Task The_grant_as_a_role_or_a_scope_opens_the_partner_webhook(string claimType, string claimValue)
    {
        using var client = ClientWith(new Claim(claimType, claimValue));

        var response = await Notify(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("role", PartnerGrant + ".extra")]
    [InlineData("scope", "x" + PartnerGrant)]
    [InlineData("scope", "payments.partner")]
    [InlineData("name", PartnerGrant)]
    [InlineData("role", OperationsGrant)]
    public async Task Near_misses_and_the_other_grant_do_not_open_the_partner_webhook(string claimType, string claimValue)
    {
        using var client = ClientWith(new Claim(claimType, claimValue));

        Assert.Equal(HttpStatusCode.Forbidden, (await Notify(client)).StatusCode);
    }

    [Theory]
    [InlineData("/ledger/balances/customer-funds/EUR")]
    [InlineData("/payments/00000000-0000-0000-0000-000000000001/ledger")]
    public async Task Ledger_reads_need_the_operations_grant(string path)
    {
        using var plain = ClientWith();
        using var partner = ClientWith(new Claim("role", PartnerGrant));
        using var operations = ClientWith(new Claim("role", OperationsGrant));

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await partner.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await operations.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task A_reconciliation_run_is_refused_before_it_reaches_the_partner_without_the_grant()
    {
        using var plain = ClientWith();

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync("/reconciliation/runs/2026-10-14", content: null)).StatusCode);
    }

    [Fact]
    public async Task The_operations_grant_lets_a_reconciliation_run_through_to_its_handler()
    {
        using var operations = ClientWith(new Claim("role", OperationsGrant));

        var response = await operations.PostAsync("/reconciliation/runs/2026-10-14", content: null);

        // The handler asks the simulated partner, which is not running here, so it answers an error of
        // its own; what matters is that authorization did not stop it.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_grant_names_are_configuration_not_code()
    {
        void Rename(IWebHostBuilder builder)
        {
            builder.UseSetting("Auth:Grants:Operations", "ops-team");
            builder.UseSetting("Auth:Grants:PartnerNotifications", "bank-webhook");
        }

        using var defaults = ClientWith(Rename, new Claim("role", OperationsGrant), new Claim("role", PartnerGrant));
        using var renamed = ClientWith(Rename, new Claim("role", "ops-team"), new Claim("role", "bank-webhook"));

        Assert.Equal(HttpStatusCode.Forbidden, (await defaults.GetAsync("/ledger/balances/customer-funds/EUR")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Notify(defaults)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await renamed.GetAsync("/ledger/balances/customer-funds/EUR")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Notify(renamed)).StatusCode);
    }

    [Fact]
    public async Task Initiating_and_reading_payments_stay_open_to_any_authenticated_caller()
    {
        using var client = ClientWith();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new
            {
                endToEndId = "E2E-PLAIN",
                amount = 10.00m,
                currency = "EUR",
                debtorAccount = "DE89370400440532013000",
                creditorAccount = "GB33BUKB20201555555555",
                scheme = "sepa",
            }),
        };
        request.Headers.Add("Idempotency-Key", "plain-caller");

        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/payments")).StatusCode);
    }

    [Theory]
    [InlineData("POST", "/partner/notifications")]
    [InlineData("GET", "/ledger/balances/customer-funds/EUR")]
    [InlineData("GET", "/payments/00000000-0000-0000-0000-000000000001/ledger")]
    [InlineData("POST", "/reconciliation/runs/2026-10-14")]
    public async Task In_the_development_anonymous_mode_the_scoped_endpoints_are_open_like_everything_else(string method, string path)
    {
        using var client = _host.Development().CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST" && path.StartsWith("/partner", StringComparison.Ordinal))
        {
            request.Content = JsonContent.Create(new { notificationId = "N-dev", endToEndId = "E2E-UNKNOWN", kind = "Settled" });
        }

        var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void Anonymous_mode_on_fly_refuses_to_start_even_if_the_environment_says_development()
    {
        var factory = _host.Host("Development", authority: null, configure: builder => builder.UseSetting("FLY_APP_NAME", "payments-bench-api"));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("FLY_APP_NAME", failure.ToString());
        Assert.Contains("Auth__Authority", failure.ToString());
    }

    [Fact]
    public async Task A_configured_authority_on_fly_starts_normally()
    {
        using var client = _host.Jwt(builder => builder.UseSetting("FLY_APP_NAME", "payments-bench-api")).CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}
