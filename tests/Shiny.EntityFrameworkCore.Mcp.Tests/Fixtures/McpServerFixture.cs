using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using Xunit;

namespace Shiny.EntityFrameworkCore.Mcp.Tests.Fixtures;

[Description("Customer orders")]
public class Order
{
    public int Id { get; set; }
    public string TenantId { get; set; } = "";

    [Description("Who placed it")]
    public string Customer { get; set; } = "";

    public decimal Total { get; set; }
    public string? Secret { get; set; }
}

public class Note
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
}

public class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Order>(e => e.Property(o => o.Total).HasPrecision(18, 2));
}

public class NotesDbContext(DbContextOptions<NotesDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Note>();
}

/// <summary>Captures log lines so a test can read the audit trail.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Lines.Enqueue((category, logLevel, formatter(state, exception)));
    }
}

/// <summary>
/// A real MCP server over the Streamable-HTTP transport, hosted in-process on a TestServer, with a real MCP client on
/// the other end. The caller's tenant arrives as a claim on <see cref="HttpContext.User"/> — set here by a stand-in for
/// an authentication handler — and the scope reads it through <see cref="IHttpContextAccessor"/>.
/// </summary>
public sealed class McpServerFixture : IAsyncDisposable
{
    public const string TenantClaim = "tenant";

    readonly WebApplication app;
    readonly SqliteConnection keepAlive;
    readonly string connectionString = $"Data Source=mcp_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    public McpServerFixture(Action<IMcpServerBuilder> configure, Action<IServiceCollection>? services = null)
    {
        this.keepAlive = new SqliteConnection(this.connectionString);
        this.keepAlive.Open();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(this.Logs);
        builder.WebHost.UseTestServer();

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDbContext<ShopDbContext>(o => o.UseSqlite(this.connectionString));
        builder.Services.AddDbContext<NotesDbContext>(o => o.UseSqlite(this.connectionString));
        services?.Invoke(builder.Services);

        configure(builder.Services.AddMcpServer().WithHttpTransport());

        this.app = builder.Build();

        // Stand-in for real authentication: whatever scheme you use, the result is a principal on HttpContext.User.
        this.app.Use((context, next) =>
        {
            var tenant = context.Request.Headers["X-Tenant"].ToString();
            if (tenant.Length > 0)
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, $"user-of-{tenant}"), new Claim(TenantClaim, tenant)], "test"));
            return next(context);
        });
        this.app.MapMcp("/mcp");
        this.app.StartAsync().GetAwaiter().GetResult();

        using var scope = this.app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShopDbContext>().Database.EnsureCreated();
        var notes = scope.ServiceProvider.GetRequiredService<NotesDbContext>();
        notes.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"Note\" (\"Id\" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, \"Text\" TEXT NOT NULL)");
    }

    public CapturingLoggerProvider Logs { get; } = new();

    public IServiceProvider Services => this.app.Services;

    /// <summary>The request-resolved scope every test uses: the tenant claim of the calling user, or refuse.</summary>
    public static void ScopeByTenantClaim(Shiny.EntityFrameworkCore.AI.IEntityAIToolBuilder<Order> t)
        => t.Where<IHttpContextAccessor>((http, _) =>
        {
            var tenant = http.HttpContext?.User.FindFirst(TenantClaim)?.Value
                ?? throw new UnauthorizedAccessException("The caller has no tenant claim.");
            return o => o.TenantId == tenant;
        });

    public Task<McpClient> Connect(string? tenant = null)
    {
        var http = this.app.GetTestClient();
        if (tenant != null)
            http.DefaultRequestHeaders.Add("X-Tenant", tenant);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp") },
            http,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        return McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    public async Task Seed()
    {
        await using var scope = this.app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        db.AddRange(
            new Order { TenantId = "acme", Customer = "Alice", Total = 30m, Secret = "hunter2" },
            new Order { TenantId = "globex", Customer = "Bob", Total = 25m, Secret = "hunter2" },
            new Order { TenantId = "acme", Customer = "Carol", Total = 35m, Secret = "hunter2" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async Task<T> Query<T>(Func<ShopDbContext, Task<T>> query)
    {
        await using var scope = this.app.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ShopDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        await this.keepAlive.DisposeAsync();
    }
}
