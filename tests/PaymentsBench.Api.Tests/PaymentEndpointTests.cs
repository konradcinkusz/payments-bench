using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaymentsBench.Api.Tests;

public sealed class PaymentEndpointTests : ApiTestBase
{
    [Fact]
    public async Task A_valid_initiation_returns_201_with_a_location_and_the_payment()
    {
        var response = await InitiateAsync(ValidBody("E2E-VALID-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Initiated", body.GetProperty("status").GetString());
        Assert.Equal("E2E-VALID-1", body.GetProperty("endToEndId").GetString());
        Assert.Equal($"/payments/{body.GetProperty("id").GetGuid()}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task An_invalid_body_returns_a_validation_problem_grouped_by_member()
    {
        var response = await InitiateAsync(new
        {
            endToEndId = new string('x', 36),
            amount = 10.123m,
            currency = "eur",
            debtorAccount = new string('d', 35),
            creditorAccount = "",
        });

        AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(["amount", "creditoraccount", "currency", "debtoraccount", "endtoendid", "scheme"], await InvalidMembersAsync(response));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_non_positive_amount_is_a_validation_problem(decimal amount)
    {
        var response = await InitiateAsync(ValidBody(amount: amount));

        AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(["amount"], await InvalidMembersAsync(response));
    }

    [Fact]
    public async Task Two_decimal_places_are_accepted_and_trailing_zeros_do_not_count()
    {
        Assert.Equal(HttpStatusCode.Created, (await InitiateAsync(ValidBody(amount: 0.01m))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await InitiateAsync(ValidBody(amount: 1.500m))).StatusCode);
    }

    [Fact]
    public async Task A_missing_idempotency_key_is_a_400_problem()
    {
        var response = await Client.PostAsJsonAsync("/payments", ValidBody());

        AssertProblem(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_over_long_idempotency_key_is_a_400_problem()
    {
        var response = await InitiateAsync(ValidBody(), idempotencyKey: new string('k', 256));

        AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Contains("idempotencykey", await InvalidMembersAsync(response));
    }

    [Fact]
    public async Task A_key_of_255_characters_is_accepted()
    {
        var response = await InitiateAsync(ValidBody(), idempotencyKey: new string('k', 255));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_a_409_problem()
    {
        var key = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Created, (await InitiateAsync(ValidBody("E2E-IDEM-A"), key)).StatusCode);

        var conflict = await InitiateAsync(ValidBody("E2E-IDEM-B"), key);

        AssertProblem(conflict, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Replaying_a_key_with_the_same_body_returns_the_original_payment()
    {
        var key = Guid.NewGuid().ToString("N");
        var body = ValidBody("E2E-IDEM-REPLAY");
        var first = await (await InitiateAsync(body, key)).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await InitiateAsync(body, key)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_unknown_payment_id_is_a_404_problem_on_every_route_that_takes_one()
    {
        var id = Guid.NewGuid();

        AssertProblem(await Client.GetAsync($"/payments/{id}"), HttpStatusCode.NotFound);
        AssertProblem(await Client.PostAsync($"/payments/{id}/submit", content: null), HttpStatusCode.NotFound);
        AssertProblem(await Client.PostAsync($"/payments/{id}/resolve", content: null), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_route_is_a_problem_body_too()
    {
        AssertProblem(await Client.GetAsync("/no-such-route"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Submitting_a_payment_twice_is_a_409_problem()
    {
        var payment = await CreatePaymentAsync();
        var id = payment.GetProperty("id").GetGuid();
        var first = await Client.PostAsync($"/payments/{id}/submit", content: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await Client.PostAsync($"/payments/{id}/submit", content: null);

        AssertProblem(second, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Malformed_json_is_a_400_problem()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments") { Content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", "k-malformed");

        AssertProblem(await Client.SendAsync(request), HttpStatusCode.BadRequest);
    }
}
