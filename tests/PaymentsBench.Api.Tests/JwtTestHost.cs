using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace PaymentsBench.Api.Tests;

/// <summary>
/// Hosts the real API the way a deployment runs it: an authority is configured, so every endpoint
/// needs a token. The test stands in for authservice: it holds an RSA key generated at run time
/// (nothing is written anywhere), publishes the public half as the JWKS and signs tokens with the
/// private half. Production code carries no test hook; the configuration manager is replaced in
/// the test host only.
/// </summary>
public sealed class JwtTestHost : IDisposable
{
    public const string Authority = "https://auth.test";
    public const string Issuer = "PaymentsBenchTest";
    public const string Audience = "PaymentsBenchTest";

    private readonly List<WebApplicationFactory<ApiEntryPoint>> _factories = [];

    public RSA SigningKey { get; } = RSA.Create(2048);

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        SigningKey.Dispose();
    }

    /// <summary>The deployed shape: Production, authority configured, JWKS published by this host.</summary>
    public WebApplicationFactory<ApiEntryPoint> Jwt(Action<IWebHostBuilder>? configure = null) =>
        Host("Production", Authority, Issuer, Audience, publishKey: true, configure);

    public WebApplicationFactory<ApiEntryPoint> Development() => Host("Development", authority: null);

    public WebApplicationFactory<ApiEntryPoint> Host(
        string environment,
        string? authority,
        string? issuer = null,
        string? audience = null,
        bool publishKey = false,
        Action<IWebHostBuilder>? configure = null)
    {
        var baseFactory = new WebApplicationFactory<ApiEntryPoint>();
        _factories.Add(baseFactory);
        var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            // Settings, not ConfigureAppConfiguration: registration reads them before the host is built.
            builder.UseSetting("Auth:Authority", authority);
            builder.UseSetting("Auth:Issuer", issuer);
            builder.UseSetting("Auth:Audience", audience);
            if (publishKey)
            {
                // Stands in for GET {authority}/.well-known/openid-configuration and its jwks_uri:
                // the handler gets this configuration instead of fetching one over HTTP. Set on the manager
                // because the options post-configuration has already built one from MetadataAddress.
                builder.ConfigureServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(new RsaSecurityKey(SigningKey.ExportParameters(includePrivateParameters: false)) { KeyId = "test-key" });
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                }));
            }

            configure?.Invoke(builder);
        });
        _factories.Add(factory);
        return factory;
    }

    /// <summary>A token signed with this host's key for subject <paramref name="subject"/>, plus any extra claims (roles, scopes).</summary>
    public string Token(string subject = "test-user", params Claim[] claims) =>
        Token(SigningKey, subject: subject, claims: claims);

    public static string Token(RSA key, string audience = Audience, string algorithm = SecurityAlgorithms.RsaSha256, string subject = "test-user", params Claim[] claims)
    {
        var credentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = "test-key" }, algorithm);
        var token = new JwtSecurityToken(Issuer, audience, claims: [new("sub", subject), .. claims], expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
