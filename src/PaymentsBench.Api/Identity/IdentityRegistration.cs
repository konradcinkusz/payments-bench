using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace PaymentsBench.Api.Identity;

/// <summary>How requests are authenticated in this process; fixed at start-up and logged.</summary>
internal sealed record AuthMode(bool Anonymous, string? Authority)
{
    public string Description => Anonymous ? "development-policy (anonymous allowed)" : $"jwt (authority {Authority})";
}

/// <summary>
/// Identity: this service holds no key. Tokens are issued by authservice (RS256) and validated
/// here against the JWKS that its OpenID Connect metadata points at.
/// </summary>
internal static class IdentityRegistration
{
    public const string AuthorityKey = "Auth:Authority";
    public const string IssuerKey = "Auth:Issuer";
    public const string AudienceKey = "Auth:Audience";

    public static WebApplicationBuilder AddPaymentsIdentity(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var authority = configuration[AuthorityKey]?.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(authority))
        {
            // Deliberately not P8 "degrade" outside Development: a deployed payment API without
            // authentication is open, not degraded (ADR 0008).
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"Auth__Authority is not set (environment '{builder.Environment.EnvironmentName}'). "
                    + "Outside Development the API refuses to start without an authority to validate tokens against.");
            }

            builder.Services.AddSingleton(new AuthMode(Anonymous: true, Authority: null));
            builder.Services.AddAuthorization();
            return builder;
        }

        var issuer = Required(configuration, IssuerKey, "Auth__Issuer");
        var audience = Required(configuration, AudienceKey, "Auth__Audience");
        var requireHttps = !builder.Environment.IsDevelopment();

        builder.Services.AddSingleton(new AuthMode(Anonymous: false, Authority: authority));
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MetadataAddress = $"{authority}/.well-known/openid-configuration";
                options.RequireHttpsMetadata = requireHttps;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return builder;
    }

    /// <summary>OpenAPI is open for local exploration only; anywhere else it needs a token like everything else.</summary>
    public static TBuilder AllowAnonymousInDevelopment<TBuilder>(this TBuilder endpoint, IHostEnvironment environment)
        where TBuilder : IEndpointConventionBuilder =>
        environment.IsDevelopment() ? endpoint.AllowAnonymous() : endpoint;

    public static void LogAuthMode(this IServiceProvider services)
    {
        var mode = services.GetRequiredService<AuthMode>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PaymentsBench.Identity");
        if (mode.Anonymous)
        {
            logger.LogWarning("auth: {Mode}. No '{Setting}': every endpoint is open. This is allowed in Development only.", mode.Description, AuthorityKey);
        }
        else
        {
            logger.LogInformation("auth: {Mode}.", mode.Description);
        }
    }

    private static string Required(ConfigurationManager configuration, string key, string envName) =>
        string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException($"{envName} is not set. It is required whenever Auth__Authority is set, so tokens meant for another product are refused.")
            : configuration[key]!;
}
