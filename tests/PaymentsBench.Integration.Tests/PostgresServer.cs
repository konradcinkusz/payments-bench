using Npgsql;
using Testcontainers.PostgreSql;

namespace PaymentsBench.Integration.Tests;

/// <summary>
/// Resolves the PostgreSQL server once per test run. <c>PAYMENTSBENCH_TEST_POSTGRES</c> wins when
/// set (CI's service container, a local server); otherwise a Testcontainers PostgreSQL starts.
/// There is no third path: no database means the tests fail, they are never skipped.
/// </summary>
internal static class PostgresServer
{
    public const string EnvironmentVariable = "PAYMENTSBENCH_TEST_POSTGRES";
    public const string Image = "postgres:16-alpine";

    private static readonly Lazy<Task<string>> Server = new(StartAsync);

    static PostgresServer() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopContainer();

    private static PostgreSqlContainer? _container;

    /// <summary>A connection string to the server's maintenance database.</summary>
    public static Task<string> ResolveAsync() => Server.Value;

    private static async Task<string> StartAsync()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new NpgsqlConnectionStringBuilder(configured) { Database = "postgres" }.ConnectionString;
        }

        try
        {
            _container = new PostgreSqlBuilder(Image).Build();
            await _container.StartAsync();
            return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = "postgres" }.ConnectionString;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"No PostgreSQL available for the integration tests. Either start Docker so Testcontainers can run '{Image}', "
                + $"or point {EnvironmentVariable} at a server, for example "
                + $"{EnvironmentVariable}=\"Host=localhost;Port=5432;Username=postgres\" (a server with trust authentication). "
                + "These tests are never skipped.",
                ex);
        }
    }

    private static void StopContainer()
    {
        if (_container is not null)
        {
            _container.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
