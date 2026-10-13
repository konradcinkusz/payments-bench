using System.Net;
using System.Net.Http.Json;

namespace PaymentsBench.Api.Tests;

public sealed class PartnerNotificationEndpointTests : ApiTestBase
{
    [Fact]
    public async Task Missing_ids_and_an_unknown_kind_are_validation_problems()
    {
        var response = await Client.PostAsJsonAsync("/partner/notifications", new { notificationId = "", kind = "Exploded" });

        AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(["endtoendid", "kind", "notificationid"], await InvalidMembersAsync(response));
    }

    [Fact]
    public async Task A_notification_for_an_unknown_payment_is_acknowledged_and_reported()
    {
        var response = await Client.PostAsJsonAsync("/partner/notifications", new { notificationId = "N-1", endToEndId = "E2E-NOBODY", kind = "Settled" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("UnknownPayment", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_notification_that_does_not_fit_the_payment_state_is_a_409_problem()
    {
        var payment = await CreatePaymentAsync();
        var endToEndId = payment.GetProperty("endToEndId").GetString();

        // Still Initiated: a simulated partner cannot have settled it.
        var response = await Client.PostAsJsonAsync("/partner/notifications", new { notificationId = "N-2", endToEndId, kind = "Settled" });

        AssertProblem(response, HttpStatusCode.Conflict);
    }
}
