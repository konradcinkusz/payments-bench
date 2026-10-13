using Microsoft.EntityFrameworkCore;
using Npgsql;
using PaymentsBench.Infrastructure.Persistence;

namespace PaymentsBench.Integration.Tests;

/// <summary>
/// A fresh, uniquely named, empty database for one test class, dropped afterwards. Tests in a
/// class share it, so each test uses its own ids and keys.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    private string _maintenance = string.Empty;
    private string _databaseName = string.Empty;

    public string ConnectionString { get; private set; } = string.Empty;

    public PaymentsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(ConnectionString).Options);

    public virtual async Task InitializeAsync()
    {
        _maintenance = await PostgresServer.ResolveAsync();
        _databaseName = $"pb_test_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{_databaseName}\"");
        ConnectionString = new NpgsqlConnectionStringBuilder(_maintenance) { Database = _databaseName, Pooling = false }.ConnectionString;
    }

    public virtual async Task DisposeAsync()
    {
        if (_databaseName.Length > 0)
        {
            await ExecuteAsync($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_maintenance);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>The same database with the migrations applied by <c>MigrateAsync</c>, never <c>EnsureCreated</c>.</summary>
public sealed class MigratedPostgresFixture : PostgresFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }
}
