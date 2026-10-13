using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PaymentsBench.Integration.Tests;

/// <summary>Runs on an empty database: the migrations are what creates the schema.</summary>
public sealed class MigrationTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Migrations_apply_to_an_empty_database_and_nothing_is_pending_or_unmigrated()
    {
        await using var db = database.CreateContext();
        Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration: run `dotnet ef migrations add`.");
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), id => id.EndsWith("_InitialSchema", StringComparison.Ordinal));

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT "MigrationId" FROM "__EFMigrationsHistory" """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var history = new List<string>();
        while (await reader.ReadAsync())
        {
            history.Add(reader.GetString(0));
        }

        Assert.Contains(history, id => id.EndsWith("_InitialSchema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrating_again_is_a_no_op()
    {
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
