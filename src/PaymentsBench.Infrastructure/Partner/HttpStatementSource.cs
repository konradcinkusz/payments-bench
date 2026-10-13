using System.Net.Http.Json;
using PaymentsBench.Application.Ports;

namespace PaymentsBench.Infrastructure.Partner;

public sealed class HttpStatementSource(HttpClient client) : IStatementSource
{
    public async Task<Statement> GetAsync(DateOnly statementDate, CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<Statement>(new Uri($"/statements/{statementDate:yyyy-MM-dd}", UriKind.Relative), PartnerJson.Options, cancellationToken)
            ?? throw new HttpRequestException("Partner returned an empty statement.");
}
