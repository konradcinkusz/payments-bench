using System.Net.Http.Headers;
using System.Net.Http.Json;
using PaymentsBench.Application.Ports;
using PaymentsBench.Iso20022;

namespace PaymentsBench.Infrastructure.Partner;

/// <summary>Which representation of the statement we ask the simulated partner for (<c>Partner:StatementFormat</c>).</summary>
public enum StatementFormat
{
    /// <summary>The partner's own JSON reduction. The default.</summary>
    Json = 0,

    /// <summary>A <c>camt.053.001.08</c> document, read at the edge by <see cref="Camt053Reader"/> (P11).</summary>
    Camt053 = 1,
}

public sealed record PartnerStatementSettings(StatementFormat Format)
{
    public const string ConfigurationKey = "Partner:StatementFormat";

    public static PartnerStatementSettings From(string? configured) => configured switch
    {
        null or "" => new PartnerStatementSettings(StatementFormat.Json),
        _ when string.Equals(configured, "json", StringComparison.OrdinalIgnoreCase) => new PartnerStatementSettings(StatementFormat.Json),
        _ when string.Equals(configured, "camt053", StringComparison.OrdinalIgnoreCase) => new PartnerStatementSettings(StatementFormat.Camt053),
        _ => throw new InvalidOperationException($"{ConfigurationKey} must be 'json' or 'camt053', not '{configured}'."),
    };
}

public sealed class HttpStatementSource(HttpClient client, PartnerStatementSettings settings) : IStatementSource
{
    public async Task<Statement> GetAsync(DateOnly statementDate, CancellationToken cancellationToken)
    {
        var camt053 = settings.Format == StatementFormat.Camt053;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/statements/{statementDate:yyyy-MM-dd}", UriKind.Relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(camt053 ? "application/xml" : "application/json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (camt053)
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await Camt053Reader.ReadAsync(body, Camt053Reader.DefaultMaxBytes, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<Statement>(PartnerJson.Options, cancellationToken)
            ?? throw new HttpRequestException("Partner returned an empty statement.");
    }
}
