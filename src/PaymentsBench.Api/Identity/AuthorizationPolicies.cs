using Microsoft.AspNetCore.Authorization;

namespace PaymentsBench.Api.Identity;

/// <summary>
/// The endpoints that are not "any authenticated caller". There is no ownership model yet (SEC-01 in
/// docs/security/SECURITY-REVIEW.md), so what only an operator or the partner may do is separated
/// by a grant on the token, not by who the caller is.
/// </summary>
internal static class AuthorizationPolicies
{
    /// <summary><c>POST /partner/notifications</c>: moves money between ledger accounts on the partner's word.</summary>
    public const string PartnerNotifications = "partner-notifications";

    /// <summary>Reconciliation runs and the ledger (entries of any payment, balances of any account): back-office reads.</summary>
    public const string Operations = "operations";

    public const string PartnerNotificationsGrantKey = "Auth:Grants:PartnerNotifications";
    public const string OperationsGrantKey = "Auth:Grants:Operations";

    public const string DefaultPartnerNotificationsGrant = "payments.partner.notify";
    public const string DefaultOperationsGrant = "payments.operate";

    /// <summary>
    /// Registers both policies. With a real authority each needs the grant on the token, on top of an
    /// authenticated user; in the Development anonymous mode there is no authentication scheme to
    /// challenge, so both are satisfied by anyone, which is the whole point of that mode (ADR 0015).
    /// </summary>
    public static IServiceCollection AddPaymentsAuthorizationPolicies(this IServiceCollection services, IConfiguration configuration, bool anonymous)
    {
        var partnerGrant = GrantOrDefault(configuration, PartnerNotificationsGrantKey, DefaultPartnerNotificationsGrant);
        var operationsGrant = GrantOrDefault(configuration, OperationsGrantKey, DefaultOperationsGrant);

        services.AddSingleton<IAuthorizationHandler, GrantHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(PartnerNotifications, policy => Configure(policy, partnerGrant, anonymous))
            .AddPolicy(Operations, policy => Configure(policy, operationsGrant, anonymous));
        return services;
    }

    private static void Configure(AuthorizationPolicyBuilder policy, string grant, bool anonymous)
    {
        if (anonymous)
        {
            policy.RequireAssertion(_ => true);
            return;
        }

        policy.RequireAuthenticatedUser().AddRequirements(new GrantRequirement(grant));
    }

    private static string GrantOrDefault(IConfiguration configuration, string key, string fallback) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? fallback : configuration[key]!.Trim();
}

/// <summary>The token must carry <see cref="Grant"/> as a role or a scope.</summary>
internal sealed record GrantRequirement(string Grant) : IAuthorizationRequirement;

/// <summary>
/// A grant is present when it is a value of the <c>role</c> / <c>roles</c> claim, or one of the
/// space-separated values of the <c>scope</c> / <c>scp</c> claim. Both shapes are accepted because the
/// token's shape is the identity provider's choice; claim names are as issued, inbound mapping is off.
/// </summary>
internal sealed class GrantHandler : AuthorizationHandler<GrantRequirement>
{
    private static readonly string[] RoleClaims = ["role", "roles"];
    private static readonly string[] ScopeClaims = ["scope", "scp"];

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, GrantRequirement requirement)
    {
        var granted = context.User.Claims.Any(claim =>
            (RoleClaims.Contains(claim.Type, StringComparer.Ordinal) && string.Equals(claim.Value, requirement.Grant, StringComparison.Ordinal))
            || (ScopeClaims.Contains(claim.Type, StringComparer.Ordinal)
                && claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(requirement.Grant, StringComparer.Ordinal)));

        if (granted)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
