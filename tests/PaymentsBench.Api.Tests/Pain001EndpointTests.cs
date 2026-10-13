using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PaymentsBench.Api.Tests;

public class Pain001EndpointTests : ApiTestBase
{
    private static string Sample(string name) => File.ReadAllText(Path.Join(AppContext.BaseDirectory, "samples", name));

    private async Task<HttpResponseMessage> PostXmlAsync(string xml, string mediaType = "application/xml")
    {
        using var content = new StringContent(xml, Encoding.UTF8, mediaType);
        return await Client.PostAsync(new Uri("/payments/pain001", UriKind.Relative), content);
    }

    [Fact]
    public async Task A_pain001_document_initiates_one_payment_per_transaction()
    {
        var response = await PostXmlAsync(Sample("pain001-two-transactions.xml"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var transactions = body.GetProperty("transactions").EnumerateArray().ToList();
        Assert.Equal(["E2E-0001", "E2E-0002"], transactions.Select(t => t.GetProperty("endToEndId").GetString()));
        Assert.All(transactions, t => Assert.Equal("Initiated", t.GetProperty("status").GetString()));

        var first = await Client.GetFromJsonAsync<JsonElement>(new Uri($"/payments/{transactions[0].GetProperty("paymentId").GetGuid()}", UriKind.Relative));
        Assert.Equal(125.50m, first.GetProperty("amount").GetDecimal());
        Assert.Equal("EUR", first.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Posting_the_same_file_again_returns_the_same_payments()
    {
        var xml = Sample("pain001-two-transactions.xml");

        var firstIds = await PaymentIdsAsync(await PostXmlAsync(xml));
        var secondIds = await PaymentIdsAsync(await PostXmlAsync(xml));

        Assert.Equal(firstIds, secondIds);
        var list = await Client.GetFromJsonAsync<JsonElement>(new Uri("/payments", UriKind.Relative));
        Assert.Equal(2, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task An_unmapped_service_level_is_a_400_problem_naming_the_element_and_creates_nothing()
    {
        var response = await PostXmlAsync(Sample("pain001-unmapped-service-level.xml"));

        AssertProblem(response, HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Document/CstmrCdtTrfInitn/PmtInf[1]/PmtTpInf/SvcLvl/Cd", problem.GetProperty("element").GetString());
        Assert.Contains("Document/CstmrCdtTrfInitn/PmtInf[1]/PmtTpInf/SvcLvl/Cd", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        var list = await Client.GetFromJsonAsync<JsonElement>(new Uri("/payments", UriKind.Relative));
        Assert.Equal(0, list.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("this is not xml", "Document")]
    [InlineData("<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:pain.001.001.03\"/>", "Document")]
    public async Task A_malformed_document_or_the_wrong_namespace_is_a_400_problem(string xml, string element)
    {
        var response = await PostXmlAsync(xml);

        AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(element, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("element").GetString());
    }

    [Fact]
    public async Task A_missing_element_and_a_bad_amount_are_400_problems_naming_the_element()
    {
        var xml = Sample("pain001-two-transactions.xml");

        var missing = await PostXmlAsync(xml.Replace("<EndToEndId>E2E-0001</EndToEndId>", string.Empty, StringComparison.Ordinal));
        var bad = await PostXmlAsync(xml.Replace(">125.50<", ">12x.50<", StringComparison.Ordinal));

        AssertProblem(missing, HttpStatusCode.BadRequest);
        Assert.Equal("Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[1]/PmtId/EndToEndId", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("element").GetString());
        AssertProblem(bad, HttpStatusCode.BadRequest);
        Assert.Equal("Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[1]/Amt/InstdAmt", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("element").GetString());
    }

    [Fact]
    public async Task A_document_with_a_dtd_is_a_400_problem()
    {
        var xml = Sample("pain001-two-transactions.xml")
            .Replace("<Document ", "<!DOCTYPE Document [<!ENTITY xxe SYSTEM \"file:///etc/hostname\">]>\n<Document ", StringComparison.Ordinal)
            .Replace("Example Initiator Ltd", "&xxe;", StringComparison.Ordinal);

        AssertProblem(await PostXmlAsync(xml), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_transaction_the_handler_refuses_is_reported_and_the_others_still_go_through()
    {
        var xml = Sample("pain001-two-transactions.xml").Replace("Ccy=\"EUR\">40.00", "Ccy=\"GBP\">40.00", StringComparison.Ordinal);

        var response = await PostXmlAsync(xml);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var transactions = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transactions").EnumerateArray().ToList();
        Assert.Equal("Initiated", transactions[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, transactions[1].GetProperty("paymentId").ValueKind);
        Assert.Equal(422, transactions[1].GetProperty("problem").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_replayed_transaction_with_a_different_amount_is_an_idempotency_conflict_in_the_result()
    {
        var xml = Sample("pain001-two-transactions.xml");
        (await PostXmlAsync(xml)).EnsureSuccessStatusCode();

        var response = await PostXmlAsync(xml.Replace(">40.00<", ">41.00<", StringComparison.Ordinal));

        var transactions = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transactions").EnumerateArray().ToList();
        Assert.Equal("Initiated", transactions[0].GetProperty("status").GetString());
        Assert.Equal(409, transactions[1].GetProperty("problem").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Json_is_not_accepted_on_the_pain001_endpoint()
    {
        var response = await PostXmlAsync("{}", "application/json");

        AssertProblem(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task A_body_over_the_cap_is_a_413_problem()
    {
        var padding = new string(' ', (1024 * 1024) + 1);

        var response = await PostXmlAsync(Sample("pain001-two-transactions.xml") + padding);

        AssertProblem(response, HttpStatusCode.RequestEntityTooLarge);
    }

    private static async Task<string[]> PaymentIdsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("transactions").EnumerateArray().Select(t => t.GetProperty("paymentId").GetString()!).ToArray();
    }
}
