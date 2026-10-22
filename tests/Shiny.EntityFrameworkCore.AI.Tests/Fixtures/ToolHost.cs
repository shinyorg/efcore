using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Shiny.EntityFrameworkCore.AI.Tests.Fixtures;

/// <summary>The per-request service a resolved scope reads — the multi-tenant story in miniature.</summary>
public interface ITenantContext
{
    string TenantId { get; set; }
}

public sealed class TenantContext : ITenantContext
{
    public string TenantId { get; set; } = "";
}

/// <summary>Records every command EF sends, so a test can prove where a predicate ended up.</summary>
public sealed class SqlCapture : DbCommandInterceptor
{
    readonly ConcurrentQueue<string> commands = new();

    public IReadOnlyList<string> Commands => this.commands.ToArray();
    public string Last => this.commands.Last();

    public void Clear() => this.commands.Clear();

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        this.commands.Enqueue(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        this.commands.Enqueue(command.CommandText);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        this.commands.Enqueue(command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>A container with the tools registered over a <see cref="DatabaseFixture"/>, plus invocation helpers.</summary>
public sealed class ToolHost : IAsyncDisposable
{
    readonly DatabaseFixture database;

    public ToolHost(
        DatabaseFixture database,
        Action<IDbContextAIToolBuilder> configure,
        Action<IServiceCollection>? services = null,
        bool useFactory = false)
    {
        this.database = database;
        var collection = new ServiceCollection();
        collection.AddScoped<ITenantContext, TenantContext>();
        services?.Invoke(collection);

        if (useFactory)
            collection.AddDbContextFactory<TestDbContext>(o => this.Configure(o));
        else
            collection.AddDbContext<TestDbContext>(o => this.Configure(o));

        collection.AddDbContextAITools<TestDbContext>(configure);
        this.Services = collection.BuildServiceProvider();
        this.Tools = this.Services.GetRequiredService<DbContextAITools<TestDbContext>>();
    }

    public SqlCapture Sql { get; } = new();
    public ServiceProvider Services { get; }
    public DbContextAITools<TestDbContext> Tools { get; }

    void Configure(DbContextOptionsBuilder builder)
    {
        this.database.Configure(builder);
        builder.AddInterceptors(this.Sql);
    }

    public AIFunction Tool(string name)
        => (AIFunction)this.Tools.Tools.Single(t => t.Name == name);

    public IReadOnlyList<string> ToolNames => this.Tools.Tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>Invokes a tool inside a fresh DI scope whose tenant is <paramref name="tenant"/>.</summary>
    public async Task<JsonNode> Invoke(string tool, object? args = null, string tenant = "")
    {
        await using var scope = this.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().TenantId = tenant;
        var result = await this.Tool(tool).InvokeAsync(
            new AIFunctionArguments(ToArguments(args)) { Services = scope.ServiceProvider },
            TestContext.Current.CancellationToken);
        return (JsonNode)result!;
    }

    /// <summary>Invokes a tool with no services on the arguments — the plain <c>IChatClient</c> lane.</summary>
    public async Task<JsonNode> InvokeWithoutServices(string tool, object? args = null)
    {
        var result = await this.Tool(tool).InvokeAsync(new AIFunctionArguments(ToArguments(args)), TestContext.Current.CancellationToken);
        return (JsonNode)result!;
    }

    /// <summary>Arguments shaped exactly as MCP delivers them: every value a <see cref="JsonElement"/>.</summary>
    public static Dictionary<string, object?> ToArguments(object? args)
    {
        var dictionary = new Dictionary<string, object?>();
        if (args is null)
            return dictionary;

        var element = JsonSerializer.SerializeToElement(args);
        foreach (var property in element.EnumerateObject())
            dictionary[property.Name] = property.Value.Clone();
        return dictionary;
    }

    public async Task Seed(params object[] entities)
    {
        await using var context = this.database.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async Task<T?> Find<T>(params object[] key) where T : class
    {
        await using var context = this.database.CreateContext();
        return await context.Set<T>().FindAsync(key, TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => this.Services.DisposeAsync();
}

public static class Data
{
    public static string NewTenant() => "t" + Guid.NewGuid().ToString("N")[..12];

    static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Five orders in <paramref name="tenant"/> plus two in another tenant that must never be seen.</summary>
    public static (Order[] Mine, Order[] Theirs) Orders(string tenant, string otherTenant)
    {
        var mine = new[]
        {
            new Order { TenantId = tenant, CustomerName = "Alice", Status = OrderStatus.Pending, Total = 10.50m, Quantity = 1, Rating = 4.5, PlacedAt = Jan1, Secret = "s1", ShipTo = new Address { City = "Toronto", Zip = "M5V" } },
            new Order { TenantId = tenant, CustomerName = "Bob", Status = OrderStatus.Shipped, Total = 20m, Quantity = 2, Rating = 3.0, PlacedAt = Jan1.AddDays(1), Secret = "s2", ShipTo = new Address { City = "Ottawa" } },
            new Order { TenantId = tenant, CustomerName = "Carol", Status = OrderStatus.Shipped, Total = 30m, Quantity = 3, Rating = null, PlacedAt = Jan1.AddDays(2), Notes = "rush", Secret = "s3", ShipTo = new Address { City = "Toronto" } },
            new Order { TenantId = tenant, CustomerName = "Dave", Status = OrderStatus.Delivered, Total = 40m, Quantity = 4, Rating = 5.0, PlacedAt = Jan1.AddDays(3), Secret = "s4", ShipTo = new Address { City = "Montreal" } },
            new Order { TenantId = tenant, CustomerName = "Erin", Status = OrderStatus.Cancelled, Total = 50m, Quantity = 5, Rating = 1.0, PlacedAt = Jan1.AddDays(4), Secret = "s5", ShipTo = new Address { City = "Toronto" } }
        };
        var theirs = new[]
        {
            new Order { TenantId = otherTenant, CustomerName = "Mallory", Status = OrderStatus.Pending, Total = 999m, Quantity = 99, PlacedAt = Jan1, Secret = "x", ShipTo = new Address { City = "Toronto" } },
            new Order { TenantId = otherTenant, CustomerName = "Alice", Status = OrderStatus.Shipped, Total = 1000m, Quantity = 100, PlacedAt = Jan1, Secret = "x", ShipTo = new Address { City = "Ottawa" } }
        };
        return (mine, theirs);
    }
}
