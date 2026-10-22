using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using MySql.Data.MySqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace Shiny.EntityFrameworkCore.Outbox.Tests;

// ── Model ───────────────────────────────────────────────────────────────

public class Order
{
    public int Id { get; set; }                  // identity: the key is only known after the INSERT
    public string Status { get; set; } = "New";
    public decimal Total { get; set; }
}

public class Customer
{
    public Guid Id { get; set; }                 // client-generated: known before the save
    public string Name { get; set; } = "";
}

public record CustomerCreated(Guid CustomerId, string Name);
public record OrderChanged(int OrderId, string Status, OutboxOperations Operation);
public record StatusChanged(int OrderId, string From, string To);

public class TestDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Order> Orders => this.Set<Order>();
    public DbSet<Customer> Customers => this.Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.Property(x => x.Status).HasMaxLength(50);
            b.Property(x => x.Total).HasPrecision(18, 2);
        });
        modelBuilder.Entity<Customer>(b =>
        {
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100);
        });
        modelBuilder.AddOutbox();
    }
}

/// <summary>A model with no outbox mapping, for the guard tests.</summary>
public class NoOutboxDbContext(DbContextOptions<NoOutboxDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => this.Set<Customer>();
}

/// <summary>
/// A model under a naming convention that renames every column and table it is allowed to — the stand-in for
/// EFCore.NamingConventions or a house style. The outbox's explicitly pinned names must survive it.
/// </summary>
public class HostileDbContext(DbContextOptions<HostileDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => this.Set<Customer>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.Conventions.Add(_ => new ShoutingConvention());

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b => b.Property(x => x.Id).ValueGeneratedNever());
        modelBuilder.AddOutbox();
    }

    sealed class ShoutingConvention : IModelFinalizingConvention
    {
        public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
        {
            foreach (var entity in modelBuilder.Metadata.GetEntityTypes())
            {
                entity.Builder.ToTable("X_" + entity.ClrType.Name.ToUpperInvariant());
                foreach (var property in entity.GetProperties())
                    property.Builder.HasColumnName("X_" + property.Name.ToUpperInvariant());
            }
        }
    }
}

// ── Helpers ─────────────────────────────────────────────────────────────

/// <summary>A clock the test moves by hand. Starts a minute ahead of real time so messages enqueued on the system
/// clock are already eligible.</summary>
public sealed class ManualClock : TimeProvider
{
    DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60);

    public override DateTimeOffset GetUtcNow() => this.now;

    public void Advance(TimeSpan by) => this.now += by;
}

public sealed class RecordingDispatcher(Func<OutboxMessage, Task>? onDispatch = null) : IOutboxDispatcher
{
    public ConcurrentQueue<OutboxMessage> Dispatched { get; } = new();

    public async Task Dispatch(OutboxMessage message, CancellationToken cancellationToken)
    {
        this.Dispatched.Enqueue(message);
        if (onDispatch != null)
            await onDispatch(message);
    }
}

/// <summary>Throws on any INSERT into the outbox table — the "failing message insert" lever.</summary>
public sealed class FailOutboxInsertInterceptor : DbCommandInterceptor
{
    static bool IsOutboxInsert(DbCommand command)
        => command.CommandText.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)
           && command.CommandText.Contains("outbox", StringComparison.OrdinalIgnoreCase);

    static void Check(DbCommand command)
    {
        if (IsOutboxInsert(command))
            throw new InvalidOperationException("outbox insert refused");
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }
}

// ── Providers ───────────────────────────────────────────────────────────

/// <summary>One database server per provider, shared by its test class; one fresh database per test.</summary>
public abstract class ProviderFixture : IAsyncLifetime
{
    public abstract string Name { get; }

    /// <summary>Points the builder at <paramref name="database"/> on this provider.</summary>
    public abstract void Use(DbContextOptionsBuilder builder, string database);

    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public DbContextOptions<TContext> Options<TContext>(string database, Action<DbContextOptionsBuilder<TContext>>? configure = null) where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        this.Use(builder, database);
        builder.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        configure?.Invoke(builder);
        return builder.Options;
    }

    /// <summary>Creates a new, empty database holding <typeparamref name="TContext"/>'s schema.</summary>
    public async Task<string> CreateDatabase<TContext>(Func<DbContextOptions<TContext>, TContext> create) where TContext : DbContext
    {
        var database = "ob" + Guid.NewGuid().ToString("N")[..16];
        await using var context = create(this.Options<TContext>(database));
        await context.Database.EnsureCreatedAsync();
        return database;
    }
}

public sealed class SqliteFixture : ProviderFixture
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "shiny-efcore-outbox", Guid.NewGuid().ToString("N"));

    public override string Name => "SQLite";

    public override ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(this.directory);
        return ValueTask.CompletedTask;
    }

    public override void Use(DbContextOptionsBuilder builder, string database)
        => builder.UseSqlite($"Data Source={Path.Combine(this.directory, database + ".db")};Pooling=False");

    public override ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(this.directory, true);
        }
        catch (IOException)
        {
            // Best effort — the temp folder is not worth failing a run over.
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class PostgresFixture : ProviderFixture
{
    readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public override string Name => "PostgreSQL";

    public override async ValueTask InitializeAsync() => await this.container.StartAsync();

    public override void Use(DbContextOptionsBuilder builder, string database)
        => builder.UseNpgsql(new NpgsqlConnectionStringBuilder(this.container.GetConnectionString()) { Database = database }.ConnectionString);

    public override async ValueTask DisposeAsync() => await this.container.DisposeAsync();
}

public sealed class SqlServerFixture : ProviderFixture
{
    readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public override string Name => "SQL Server";

    public override async ValueTask InitializeAsync() => await this.container.StartAsync();

    public override void Use(DbContextOptionsBuilder builder, string database)
        => builder.UseSqlServer(new SqlConnectionStringBuilder(this.container.GetConnectionString())
        {
            InitialCatalog = database,
            TrustServerCertificate = true
        }.ConnectionString);

    public override async ValueTask DisposeAsync() => await this.container.DisposeAsync();
}

public sealed class MySqlFixture : ProviderFixture
{
    // root, so EnsureCreated may CREATE DATABASE for each test.
    readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").WithUsername("root").WithPassword("outbox").Build();

    public override string Name => "MySQL";

    public override async ValueTask InitializeAsync() => await this.container.StartAsync();

    public override void Use(DbContextOptionsBuilder builder, string database)
        => builder.UseMySQL(new MySqlConnectionStringBuilder(this.container.GetConnectionString()) { Database = database }.ConnectionString);

    public override async ValueTask DisposeAsync() => await this.container.DisposeAsync();
}

/// <summary>Counts transactions begun on the context's connection.</summary>
public sealed class TransactionCounter : DbTransactionInterceptor
{
    int started;

    public int Started => this.started;

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Interlocked.Increment(ref this.started);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref this.started);
        return ValueTask.FromResult(result);
    }
}
