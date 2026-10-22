using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shiny.EntityFrameworkCore.AI.Tests.Fixtures;

/// <summary>One database per fixture, schema created once. Tests isolate their rows by a unique tenant id.</summary>
public abstract class DatabaseFixture : IAsyncLifetime
{
    public abstract string Provider { get; }
    public abstract void Configure(DbContextOptionsBuilder builder);
    protected abstract ValueTask StartAsync();
    protected virtual ValueTask StopAsync() => ValueTask.CompletedTask;

    public async ValueTask InitializeAsync()
    {
        await this.StartAsync();
        await using var context = this.CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync();
        GC.SuppressFinalize(this);
    }

    public TestDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>();
        this.Configure(builder);
        return new TestDbContext(builder.Options);
    }
}

/// <summary>A private shared-cache in-memory database, held open by one keep-alive connection.</summary>
public sealed class SqliteDatabase : DatabaseFixture
{
    readonly string connectionString = $"Data Source=ai_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    SqliteConnection? keepAlive;

    public override string Provider => "Sqlite";

    public override void Configure(DbContextOptionsBuilder builder) => builder.UseSqlite(this.connectionString);

    protected override async ValueTask StartAsync()
    {
        this.keepAlive = new SqliteConnection(this.connectionString);
        await this.keepAlive.OpenAsync();
    }

    protected override async ValueTask StopAsync()
    {
        if (this.keepAlive != null)
            await this.keepAlive.DisposeAsync();
    }
}

public sealed class PostgresDatabase : DatabaseFixture
{
    readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public override string Provider => "PostgreSql";
    public override void Configure(DbContextOptionsBuilder builder) => builder.UseNpgsql(this.container.GetConnectionString());
    protected override async ValueTask StartAsync() => await this.container.StartAsync();
    protected override ValueTask StopAsync() => this.container.DisposeAsync();
}

public sealed class SqlServerDatabase : DatabaseFixture
{
    readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public override string Provider => "SqlServer";
    public override void Configure(DbContextOptionsBuilder builder) => builder.UseSqlServer(this.container.GetConnectionString());
    protected override async ValueTask StartAsync() => await this.container.StartAsync();
    protected override ValueTask StopAsync() => this.container.DisposeAsync();
}

public sealed class MySqlDatabase : DatabaseFixture
{
    readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    public override string Provider => "MySql";
    public override void Configure(DbContextOptionsBuilder builder) => builder.UseMySQL(this.container.GetConnectionString());
    protected override async ValueTask StartAsync() => await this.container.StartAsync();
    protected override ValueTask StopAsync() => this.container.DisposeAsync();
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresDatabase>;

[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerDatabase>;

[CollectionDefinition("mysql")]
public sealed class MySqlCollection : ICollectionFixture<MySqlDatabase>;
