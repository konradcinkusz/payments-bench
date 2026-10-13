using PaymentsBench.Api.Contracts;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Ledger;

namespace PaymentsBench.Api.Endpoints;

public static class LedgerEndpoints
{
    public const string Tag = "Ledger";

    public static IEndpointRouteBuilder MapLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/payments/{id:guid}/ledger", EntriesAsync)
            .WithTags(Tag)
            .WithName("GetPaymentLedger")
            .Produces<IReadOnlyList<JournalEntry>>();

        app.MapGet("/ledger/balances/{account}/{currency}", BalanceAsync)
            .WithTags(Tag)
            .WithName("GetLedgerBalance")
            .Produces<BalanceView>();
        return app;
    }

    private static async Task<IResult> EntriesAsync(Guid id, ILedger ledger, CancellationToken cancellationToken) =>
        Results.Ok(await ledger.EntriesForAsync(id, cancellationToken));

    private static async Task<IResult> BalanceAsync(string account, string currency, ILedger ledger, CancellationToken cancellationToken) =>
        Results.Ok(new BalanceView(account, currency, await ledger.BalanceAsync(account, currency, cancellationToken)));
}
