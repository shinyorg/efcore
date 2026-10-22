using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MySql.Data.MySqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace Shiny.EntityFrameworkCore.Temporal.Tests;

// ── Model ───────────────────────────────────────────────────────────────

public class Order
{
    public int Id { get; set; }                      // identity: only known after the INSERT
    public string Customer { get; set; } = "";
    public string Status { get; set; } = "New";
    public int Quantity { get; set; }
    public Money Price { get; set; } = new();        // complex type
    public Address? ShippingAddress { get; set; }    // owned reference (table splitting)
    public List<OrderLine> Lines { get; set; } = []; // owned collection (own table)
}

public class Money
{
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
}

public class Address
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
}

public class OrderLine
{
    public string Product { get; set; } = "";
    public int Qty { get; set; }
}

public class Product
{
    public string Sku { get; set; } = "";            // client-assigned key
    public string Name { get; set; } = "";
    // plus a shadow "Category" property
}

public class Enrollment
{
    public int StudentId { get; set; }               // composite key (StudentId, Course)
    public string Course { get; set; } = "";
    public string Grade { get; set; } = "";
}

public class Note
{
    public int Id { get; set; }                      // no history
    public string Text { get; set; } = "";
}

public abstract class Animal
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class Dog : Animal
{
    public string Breed { get; set; } = "";
}

public class Cat : Animal
{
    public int Lives { get; set; }
}

public class TestDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Order> Orders => this.Set<Order>();
    public DbSet<Product> Products => this.Set<Product>();
    public DbSet<Enrollment> Enrollments => this.Set<Enrollment>();
    public DbSet<Note> Notes => this.Set<Note>();
    public DbSet<Animal> Animals => this.Set<Animal>();

    /// <summary>Retention / max versions for Product, set per test through the model cache key.</summary>
    public HistoryOptions? ProductHistory { get; init; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.Property(x => x.Customer).HasMaxLength(100);
            b.Property(x => x.Status).HasMaxLength(50);
            b.ComplexProperty(x => x.Price, c =>
            {
                c.Property(p => p.Amount).HasPrecision(18, 2);
                c.Property(p => p.Currency).HasMaxLength(3);
            });
            b.OwnsOne(x => x.ShippingAddress, a =>
            {
                a.Property(p => p.Street).HasMaxLength(100);
                a.Property(p => p.City).HasMaxLength(100);
            });
            b.OwnsMany(x => x.Lines, l =>
            {
                // A single-column surrogate key: SQLite can't auto-increment the default composite (OrderId, Id).
                l.Property<int>("Id");
                l.HasKey("Id");
                l.Property(p => p.Product).HasMaxLength(100);
            });
            b.HasHistory();
        });

        modelBuilder.Entity<Product>(b =>
        {
            b.HasKey(x => x.Sku);
            b.Property(x => x.Sku).HasMaxLength(64);
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property<string>("Category").HasMaxLength(50);
            b.HasHistory(h =>
            {
                h.Name = "Product";
                h.Retention = this.ProductHistory?.Retention;
                h.MaxVersions = this.ProductHistory?.MaxVersions;
            });
        });

        modelBuilder.Entity<Enrollment>(b =>
        {
            b.HasKey(x => new { x.StudentId, x.Course });
            b.Property(x => x.StudentId).ValueGeneratedNever();
            b.Property(x => x.Course).HasMaxLength(32);
            b.Property(x => x.Grade).HasMaxLength(4);
            b.HasHistory();
        });

        modelBuilder.Entity<Note>(b => b.Property(x => x.Text).HasMaxLength(100));

        modelBuilder.Entity<Animal>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.HasHistory();
        });
        modelBuilder.Entity<Dog>(b => b.Property(x => x.Breed).HasMaxLength(100));
        modelBuilder.Entity<Cat>();
    }
}

/// <summary>Distinguishes models by the Product history settings, which are baked into the model.</summary>
public sealed class ProductHistoryModelCacheKeyFactory : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => context is TestDbContext t
            ? (context.GetType(), t.ProductHistory?.Retention, t.ProductHistory?.MaxVersions, designTime)
            : (object)(context.GetType(), designTime);
}

/// <summary>A request-scoped "current user" for the ResolveActor tests.</summary>
public sealed class CurrentUser
{
    public string? Name { get; set; }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualClock : TimeProvider
{
    DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => this.now;

    public DateTimeOffset Advance(TimeSpan by) => this.now += by;
}

/// <summary>
/// A retrying execution strategy. Like <c>EnableRetryOnFailure</c>, it forbids user-initiated transactions, so it
/// proves the history interceptor never opens one of its own.
/// </summary>
public sealed class RetryingTestStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, 3, TimeSpan.FromMilliseconds(1))
{
    protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestException;
}

public sealed class TransientTestException : Exception;

/// <summary>Throws a transient failure the first time a transaction commits so the retrying strategy has to run SaveChanges again.</summary>
public sealed class FailFirstCommitInterceptor : DbTransactionInterceptor
{
    int remaining = 1;

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(System.Data.Common.DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref this.remaining, 0) == 1)
            throw new TransientTestException();
        return ValueTask.FromResult(result);
    }
}

// ── Providers ───────────────────────────────────────────────────────────

/// <summary>One database server per provider, shared by its test class; one fresh database per test.</summary>
public abstract class ProviderFixture : IAsyncLifetime
{
    public abstract string Name { get; }

    public abstract void Use(DbContextOptionsBuilder builder, string database, bool retrying);

    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SqliteFixture : ProviderFixture
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "shiny-efcore-temporal", Guid.NewGuid().ToString("N"));

    public override string Name => "SQLite";

    public override ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(this.directory);
        return ValueTask.CompletedTask;
    }

    public override void Use(DbContextOptionsBuilder builder, string database, bool retrying)
        => builder.UseSqlite($"Data Source={Path.Combine(this.directory, database + ".db")};Pooling=False", o =>
        {
            if (retrying)
                o.ExecutionStrategy(d => new RetryingTestStrategy(d));
        });

    public override ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(this.directory, true);
        }
        catch (IOException)
        {
            // Best effort; a temp folder is not worth failing a run over.
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class PostgresFixture : ProviderFixture
{
    readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public override string Name => "PostgreSQL";

    public override async ValueTask InitializeAsync() => await this.container.StartAsync();

    public override void Use(DbContextOptionsBuilder builder, string database, bool retrying)
        => builder.UseNpgsql(new NpgsqlConnectionStringBuilder(this.container.GetConnectionString()) { Database = database }.ConnectionString, o =>
        {
            if (retrying)
                o.ExecutionStrategy(d => new RetryingTestStrategy(d));
        });

    public override async ValueTask DisposeAsync() => await this.container.DisposeAsync();
}

public sealed class SqlServerFixture : ProviderFixture
{
    readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public override string Name => "SQL Server";

    public override async ValueTask InitializeAsync() => await this.container.StartAsync();

    public override void Use(DbContextOptionsBuilder builder, string database, bool retrying)
        => builder.UseSqlServer(new SqlConnectionStringBuilder(this.container.GetConnectionString())
        {
            InitialCatalog = database,
            TrustServerCertificate = true
        }.ConnectionString, o =>
        {
            if (retrying)
                o.ExecutionStrategy(d => new RetryingTestStrategy(d));
        });

    public override async ValueTask DisposeAsync() => await this.container.DisposeAsync();
}

public sealed class MySqlFixture : ProviderFixture
{
    // root, so EnsureCreated may CREATE DATABASE for each test.
    readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").WithUsername("root").WithPassword("temporal").Build();

    public override string Name => "MySQL";

    public override async ValueTask InitializeAsync() => await this.container.StartAsync();

    public override void Use(DbContextOptionsBuilder builder, string database, bool retrying)
        => builder.UseMySQL(new MySqlConnectionStringBuilder(this.container.GetConnectionString()) { Database = database }.ConnectionString, o =>
        {
            if (retrying)
                o.ExecutionStrategy(d => new RetryingTestStrategy(d));
        });

    public override async ValueTask DisposeAsync() => await this.container.DisposeAsync();
}
