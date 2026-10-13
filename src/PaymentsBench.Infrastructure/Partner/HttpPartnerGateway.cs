using System.Net;
using System.Net.Http.Json;
using PaymentsBench.Application.Ports;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace PaymentsBench.Infrastructure.Partner;

/// <summary>
/// The partner over HTTP. Submit is sent once: a timeout, a dropped connection or a 5xx after
/// the body went out all become <see cref="SubmitOutcome.Unknown"/>, never a retry, because a
/// retried submit is a possible duplicate transfer. Status lookups are idempotent and get the
/// resilience pipeline configured in <see cref="InfrastructureRegistration"/>.
/// </summary>
public sealed class HttpPartnerGateway(HttpClient client) : IPartnerGateway
{
    public async Task<SubmitResult> SubmitAsync(PartnerTransfer transfer, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("/transfers", transfer, PartnerJson.Options, cancellationToken);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var rejection = await response.Content.ReadFromJsonAsync<PartnerRejection>(PartnerJson.Options, cancellationToken);
                return new SubmitResult(SubmitOutcome.Rejected, null, rejection?.ReasonCode ?? "partner-rejected");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new SubmitResult(SubmitOutcome.Unknown, null, $"http-{(int)response.StatusCode}");
            }

            var accepted = await response.Content.ReadFromJsonAsync<PartnerAcceptance>(PartnerJson.Options, cancellationToken);
            return accepted is null
                ? new SubmitResult(SubmitOutcome.Unknown, null, "empty-body")
                : new SubmitResult(SubmitOutcome.Accepted, accepted.PartnerReference, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutRejectedException or BrokenCircuitException
                                   && !cancellationToken.IsCancellationRequested)
        {
            // The request may or may not have reached the partner. Say so; never guess.
            return new SubmitResult(SubmitOutcome.Unknown, null, ex.GetType().Name);
        }
    }

    public async Task<PartnerStatusResult> GetStatusAsync(string endToEndId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri($"/transfers/{Uri.EscapeDataString(endToEndId)}", UriKind.Relative), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new PartnerStatusResult(PartnerStatus.NotFound, null, null, null);
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<PartnerTransferView>(PartnerJson.Options, cancellationToken)
            ?? throw new HttpRequestException("Partner returned an empty status body.");
        return new PartnerStatusResult(Enum.Parse<PartnerStatus>(body.Status, ignoreCase: true), body.PartnerReference, body.ReasonCode, body.ValueDate);
    }

    private sealed record PartnerAcceptance(string PartnerReference);

    private sealed record PartnerRejection(string ReasonCode);

    private sealed record PartnerTransferView(string Status, string? PartnerReference, string? ReasonCode, DateOnly? ValueDate);
}
