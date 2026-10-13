using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaymentsBench.ServiceDefaults;

namespace PaymentsBench.ServiceDefaults.Tests;

/// <summary>
/// The test server does not enforce Kestrel limits, so these tests pin the configuration that
/// Kestrel enforces; the enforcement itself (413) was exercised against a real Kestrel and is
/// recorded in docs/security/SECURITY-REVIEW.md (SEC-07).
/// </summary>
public sealed class RequestLimitsTests
{
    private static KestrelServerOptions Options(Dictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings ?? []);
        builder.AddServiceDefaults();
        using var app = builder.Build();
        return app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
    }

    [Fact]
    public void The_request_body_is_capped_at_64_KiB_by_default_not_kestrels_30_MB()
    {
        Assert.Equal(64 * 1024, Options().Limits.MaxRequestBodySize);
    }

    [Fact]
    public void The_cap_is_configurable()
    {
        Assert.Equal(2048, Options(new() { ["Limits:MaxRequestBodyBytes"] = "2048" }).Limits.MaxRequestBodySize);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("0")]
    [InlineData("-5")]
    public void An_unusable_value_falls_back_to_the_default_rather_than_lifting_the_cap(string value)
    {
        Assert.Equal(Extensions.DefaultMaxRequestBodyBytes, Options(new() { ["Limits:MaxRequestBodyBytes"] = value }).Limits.MaxRequestBodySize);
    }

    [Fact]
    public void The_server_header_is_not_sent()
    {
        Assert.False(Options().AddServerHeader);
    }
}
