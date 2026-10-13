using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaymentsBench.Api.Tests;

public sealed class ListPaymentsTests : ApiTestBase
{
    private async Task<JsonElement> ListAsync(string query = "")
    {
        var response = await Client.GetAsync($"/payments{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task SeedAsync()
    {
        // 3 sepa (one submitted) and 2 faster-payments, created in sequence so order is observable.
        foreach (var scheme in new[] { "sepa", "sepa", "sepa", "faster-payments", "faster-payments" })
        {
            await CreatePaymentAsync(scheme);
        }

        var sepa = await ListAsync("?scheme=sepa");
        var id = sepa.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsync($"/payments/{id}/submit", content: null)).StatusCode);
    }

    [Fact]
    public async Task Listing_returns_the_page_shape_with_total_and_counts_for_every_status()
    {
        await SeedAsync();

        var body = await ListAsync();

        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("limit").GetInt32());
        Assert.Equal(5, body.GetProperty("total").GetInt32());
        Assert.Equal(5, body.GetProperty("items").GetArrayLength());
        var counts = body.GetProperty("countsByStatus");
        Assert.Equal(4, counts.GetProperty("Initiated").GetInt32());
        Assert.Equal(1, counts.GetProperty("Accepted").GetInt32() + counts.GetProperty("Submitted").GetInt32());
        Assert.Equal(0, counts.GetProperty("Returned").GetInt32());
    }

    [Fact]
    public async Task Items_are_newest_first_and_pages_do_not_overlap()
    {
        await SeedAsync();

        var all = (await ListAsync()).GetProperty("items").EnumerateArray().ToList();
        var created = all.Select(i => i.GetProperty("createdAt").GetDateTimeOffset()).ToList();
        Assert.Equal(created.OrderByDescending(c => c), created);

        var page1 = (await ListAsync("?limit=2&page=1")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid());
        var page2 = (await ListAsync("?limit=2&page=2")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid());
        var page3 = (await ListAsync("?limit=2&page=3")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid());
        Assert.Equal(all.Select(i => i.GetProperty("id").GetGuid()), page1.Concat(page2).Concat(page3));
    }

    [Fact]
    public async Task Limit_is_clamped_to_100_and_page_to_1()
    {
        var huge = await ListAsync("?limit=1000000");
        Assert.Equal(100, huge.GetProperty("limit").GetInt32());

        var zeroPage = await ListAsync("?page=0&limit=0");
        Assert.Equal(1, zeroPage.GetProperty("page").GetInt32());
        Assert.Equal(1, zeroPage.GetProperty("limit").GetInt32());

        Assert.Equal(1, (await ListAsync("?page=-7")).GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_but_still_reports_the_total()
    {
        await SeedAsync();

        var body = await ListAsync("?page=99");

        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.Equal(5, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Filtering_by_scheme_narrows_items_total_and_counts()
    {
        await SeedAsync();

        var body = await ListAsync("?scheme=faster-payments");

        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.All(body.GetProperty("items").EnumerateArray(), i => Assert.Equal("Initiated", i.GetProperty("status").GetString()));
        Assert.Equal(2, body.GetProperty("countsByStatus").GetProperty("Initiated").GetInt32());
    }

    [Fact]
    public async Task Filtering_by_status_is_case_insensitive_and_counts_the_filtered_set()
    {
        await SeedAsync();

        var body = await ListAsync("?status=initiated");

        Assert.Equal(4, body.GetProperty("total").GetInt32());
        Assert.Equal(4, body.GetProperty("items").GetArrayLength());
        Assert.Equal(4, body.GetProperty("countsByStatus").GetProperty("Initiated").GetInt32());
        Assert.Equal(0, body.GetProperty("countsByStatus").GetProperty("Submitted").GetInt32());
    }

    [Fact]
    public async Task Filtering_by_end_to_end_id_finds_one_payment()
    {
        var payment = await CreatePaymentAsync();
        var endToEndId = payment.GetProperty("endToEndId").GetString();

        var body = await ListAsync($"?endToEndId={endToEndId}");

        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(payment.GetProperty("id").GetGuid(), body.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Filtering_by_created_range_is_inclusive_of_both_ends()
    {
        var payment = await CreatePaymentAsync();
        var createdAt = payment.GetProperty("createdAt").GetDateTimeOffset();
        var exact = Uri.EscapeDataString(createdAt.ToString("O"));

        Assert.Equal(1, (await ListAsync($"?endToEndId={payment.GetProperty("endToEndId").GetString()}&createdFrom={exact}&createdTo={exact}")).GetProperty("total").GetInt32());

        var after = Uri.EscapeDataString(createdAt.AddMinutes(1).ToString("O"));
        Assert.Equal(0, (await ListAsync($"?endToEndId={payment.GetProperty("endToEndId").GetString()}&createdFrom={after}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task An_unknown_status_is_a_400_validation_problem()
    {
        var response = await Client.GetAsync("/payments?status=Exploded");

        AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(["status"], await InvalidMembersAsync(response));
    }
}
