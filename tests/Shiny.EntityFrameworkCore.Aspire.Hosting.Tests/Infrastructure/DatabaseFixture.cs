using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MySql.Data.MySqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Shiny.EntityFrameworkCore.Aspire.Hosting.Tests.Infrastructure.DatabaseFixture))]

namespace Shiny.EntityFrameworkCore.Aspire.Hosting.Tests.Infrastructure;

/// <summary>
/// One container per server provider, started lazily, plus a temp folder for SQLite files. The gate logic is driven
/// directly against these; only the end-to-end test starts a real Aspire application.
/// </summary>
public sealed class DatabaseFixture : IAsyncDisposable
{
    public const string Sqlite = "sqlite";
    public const string Postgres = "postgres";
    public const string SqlServer = "sqlserver";
    public const string MySql = "mysql";

    public static TheoryData<string> AllProviders => new(Sqlite, Postgres, SqlServer, MySql);

    readonly string sqliteDirectory = Path.Combine(Path.GetTempPath(), $"shiny-gate-tests-{Guid.NewGuid():N}");
    readonly Lazy<Task<PostgreSqlContainer>> postgres = new(() => StartAsync(new PostgreSqlBuilder("postgres:17-alpine").Build()));
    readonly Lazy<Task<MsSqlContainer>> sqlServer = new(() => StartAsync(new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build()));
    readonly Lazy<Task<MySqlContainer>> mySql = new(() => StartAsync(new MySqlBuilder("mysql:8.4").WithUsername("root").Build()));

    public DatabaseFixture() => Directory.CreateDirectory(this.sqliteDirectory);

    /// <summary>A connection string to a database that does not exist yet.</summary>
    public async Task<TestDatabase> NewDatabaseAsync(string provider)
    {
        var name = $"gate_{Guid.NewGuid():N}"[..30];
        var connectionString = provider switch
        {
            Sqlite => new SqliteConnectionStringBuilder { DataSource = Path.Combine(this.sqliteDirectory, name + ".db"), Pooling = false }.ToString(),
            Postgres => new NpgsqlConnectionStringBuilder((await this.postgres.Value).GetConnectionString()) { Database = name, Pooling = false }.ToString(),
            SqlServer => new SqlConnectionStringBuilder((await this.sqlServer.Value).GetConnectionString()) { InitialCatalog = name, Pooling = false }.ToString(),
            MySql => new MySqlConnectionStringBuilder((await this.mySql.Value).GetConnectionString()) { Database = name, Pooling = false }.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
        return new TestDatabase(provider, connectionString);
    }

    static async Task<T> StartAsync<T>(T container) where T : DotNet.Testcontainers.Containers.IContainer
    {
        await container.StartAsync();
        return container;
    }

    public async ValueTask DisposeAsync()
    {
        if (this.postgres.IsValueCreated)
            await (await this.postgres.Value).DisposeAsync();
        if (this.sqlServer.IsValueCreated)
            await (await this.sqlServer.Value).DisposeAsync();
        if (this.mySql.IsValueCreated)
            await (await this.mySql.Value).DisposeAsync();

        try
        {
            Directory.Delete(this.sqliteDirectory, true);
        }
        catch (IOException)
        {
            // the temp folder is disposable anyway
        }
    }
}

public sealed record TestDatabase(string Provider, string ConnectionString)
{
    /// <summary>Configures <paramref name="builder"/> for this provider against <paramref name="connectionString"/>.</summary>
    public void Configure(DbContextOptionsBuilder builder, string? connectionString = null)
    {
        var cs = connectionString ?? this.ConnectionString;
        switch (this.Provider)
        {
            case DatabaseFixture.Sqlite:
                builder.UseSqlite(cs);
                break;
            case DatabaseFixture.Postgres:
                builder.UseNpgsql(cs);
                break;
            case DatabaseFixture.SqlServer:
                builder.UseSqlServer(cs);
                break;
            case DatabaseFixture.MySql:
                builder.UseMySQL(cs);
                break;
        }
        // The test migration ships without a model snapshot; EF 9+ would otherwise refuse to migrate.
        builder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    public GateDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<GateDbContext>();
        this.Configure(builder);
        return new GateDbContext(builder.Options);
    }

    public DbContext CreateMarkerContext()
    {
        var builder = new DbContextOptionsBuilder();
        this.Configure(builder);
        return new DbContext(builder.Options);
    }
}
