using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MySql.Data.MySqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Shiny.EntityFrameworkCore.Seeding.Tests.Infrastructure.DatabaseFixture))]

namespace Shiny.EntityFrameworkCore.Seeding.Tests.Infrastructure;

/// <summary>
/// One container per server provider for the whole assembly, started lazily the first time a test asks for it,
/// so the SQLite tests run without Docker. Every test gets its own database name, so history tables never leak
/// between tests.
/// </summary>
public sealed class DatabaseFixture : IAsyncDisposable
{
    public const string Sqlite = "sqlite";
    public const string Postgres = "postgres";
    public const string SqlServer = "sqlserver";
    public const string MySql = "mysql";

    public static TheoryData<string> AllProviders => new(Sqlite, Postgres, SqlServer, MySql);
    public static TheoryData<string> ServerProviders => new(Postgres, SqlServer, MySql);

    readonly string sqliteDirectory = Path.Combine(Path.GetTempPath(), $"shiny-seed-tests-{Guid.NewGuid():N}");
    readonly Lazy<Task<PostgreSqlContainer>> postgres = new(() => StartAsync(new PostgreSqlBuilder("postgres:17-alpine").Build()));
    readonly Lazy<Task<MsSqlContainer>> sqlServer = new(() => StartAsync(new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build()));
    readonly Lazy<Task<MySqlContainer>> mySql = new(() => StartAsync(new MySqlBuilder("mysql:8.4").WithUsername("root").Build()));

    public DatabaseFixture() => Directory.CreateDirectory(this.sqliteDirectory);

    /// <summary>A connection string for a brand-new (not yet created) database on <paramref name="provider"/>.</summary>
    public async Task<TestDatabase> NewDatabaseAsync(string provider)
    {
        var name = $"seed_{Guid.NewGuid():N}"[..30];
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
            // a pooled handle can outlive the test run on some platforms; the temp folder is disposable anyway
        }
    }
}

/// <summary>A per-test database: the provider plus a connection string to a database that may not exist yet.</summary>
public sealed record TestDatabase(string Provider, string ConnectionString)
{
    public void Configure(DbContextOptionsBuilder builder)
    {
        switch (this.Provider)
        {
            case DatabaseFixture.Sqlite:
                builder.UseSqlite(this.ConnectionString);
                break;
            case DatabaseFixture.Postgres:
                builder.UseNpgsql(this.ConnectionString);
                break;
            case DatabaseFixture.SqlServer:
                builder.UseSqlServer(this.ConnectionString);
                break;
            case DatabaseFixture.MySql:
                builder.UseMySQL(this.ConnectionString);
                break;
        }
        // The test migration ships without a model snapshot; EF 9+ would otherwise refuse to migrate.
        builder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    public DbContextOptions<TContext> Options<TContext>(Action<DbContextOptionsBuilder<TContext>>? configure = null) where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        this.Configure(builder);
        configure?.Invoke(builder);
        return builder.Options;
    }

    public SeedDbContext CreateContext(Action<DbContextOptionsBuilder<SeedDbContext>>? configure = null)
        => new(this.Options(configure));
}
