using Microsoft.Extensions.DependencyInjection;
using PaymentsBench.Application.Payments;

namespace PaymentsBench.Infrastructure.Sweeping;

/// <summary>Resolves one payment through <see cref="ResolveSubmittedPaymentHandler"/> in a DI scope of its own.</summary>
public sealed class ScopedSubmittedPaymentResolver(IServiceScopeFactory scopes) : ISubmittedPaymentResolver
{
    public async Task<ResolutionOutcome> ResolveAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ResolveSubmittedPaymentHandler>();
        return await handler.HandleAsync(new ResolveSubmittedPayment(paymentId), cancellationToken);
    }
}
