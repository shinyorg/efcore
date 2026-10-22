using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shiny.EntityFrameworkCore.AI.Implementation;
using Shiny.EntityFrameworkCore.AI.Tests.Fixtures;
using Xunit;

namespace Shiny.EntityFrameworkCore.AI.Tests;

/// <summary>Where each call's DbContext comes from, and the argument shapes a non-MCP caller may pass.</summary>
public class LifetimeTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    sealed class CountingFactory(DatabaseFixture database) : IDbContextFactory<TestDbContext>
    {
        int created;
        public int Created => Volatile.Read(ref this.created);

        public TestDbContext CreateDbContext()
        {
            Interlocked.Increment(ref this.created);
            return database.CreateContext();
        }
    }

    static Action<IDbContextAIToolBuilder> Orders => b => b.AddEntity<Order>(t => t
        .AllowAll()
        .Where<ITenantContext>((s, _) => o => o.TenantId == s.TenantId));

    [Fact]
    public async Task ParallelCalls_EachGetTheirOwnContext()
    {
        await using var host = new ToolHost(database, Orders);
        var tenant = Data.NewTenant();
        await host.Seed(Data.Orders(tenant, Data.NewTenant()).Mine);

        // A shared DbContext would throw "a second operation was started on this context instance".
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => i % 2 == 0
            ? host.Invoke("order_query", tenant: tenant)
            : host.Invoke("order_aggregate", new { function = "sum", field = "quantity" }, tenant)));

        Assert.All(results.Where((_, i) => i % 2 == 0), r => Assert.Equal(5, r["count"]!.GetValue<int>()));
        Assert.All(results.Where((_, i) => i % 2 == 1), r => Assert.Equal(15, r["value"]!.GetValue<int>()));
    }

    [Fact]
    public async Task ARegisteredFactory_IsPreferredOverTheScopedContext()
    {
        var factory = new CountingFactory(database);
        await using var host = new ToolHost(database, Orders, services => services.AddSingleton<IDbContextFactory<TestDbContext>>(factory));
        var before = factory.Created;

        await host.Invoke("order_count", tenant: "x");
        await host.Invoke("order_count", tenant: "x");

        Assert.Equal(before + 2, factory.Created);
    }

    [Fact]
    public async Task AddDbContextFactory_Works()
    {
        await using var host = new ToolHost(database, Orders, useFactory: true);
        var tenant = Data.NewTenant();

        var inserted = await host.Invoke("order_insert", new { document = new { tenantId = tenant, customerName = "Via factory" } }, tenant);
        Assert.True(inserted["inserted"]!.GetValue<bool>());
        Assert.Equal(1, (await host.Invoke("order_count", tenant: tenant))["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task WritesAreVisibleToTheNextCall_NothingLingersInATracker()
    {
        await using var host = new ToolHost(database, Orders);
        var tenant = Data.NewTenant();
        var (mine, _) = Data.Orders(tenant, Data.NewTenant());
        await host.Seed(mine);

        await host.Invoke("order_update", new { document = new { id = mine[0].Id, quantity = 42 } }, tenant);
        var got = await host.Invoke("order_get_by_id", new { id = mine[0].Id }, tenant);

        Assert.Equal(42, got["item"]!["quantity"]!.GetValue<int>());
    }

    [Fact]
    public async Task ArgumentsMayBeClrValues_JsonStrings_OrJsonNodes()
    {
        await using var host = new ToolHost(database, b => b.AddEntity<Order>(t => t.AllowQuery().Where(o => o.TenantId == "shapes")));
        await host.Seed(Data.Orders("shapes", Data.NewTenant()).Mine);
        var query = host.Tool("order_query");

        async Task<int> Count(Dictionary<string, object?> args)
            => ((JsonNode)(await query.InvokeAsync(new AIFunctionArguments(args), TestContext.Current.CancellationToken))!)["count"]!.GetValue<int>();

        Assert.Equal(2, await Count(new() { ["filter"] = """{"field":"quantity","op":"gt","value":3}""", ["limit"] = 10 }));
        Assert.Equal(2, await Count(new() { ["filter"] = JsonNode.Parse("""{"field":"quantity","op":"gt","value":"3"}"""), ["limit"] = 10L }));
        Assert.Equal(1, await Count(new() { ["limit"] = "1", ["orderBy"] = "quantity", ["orderDirection"] = "desc" }));
        Assert.Equal(5, await Count(new() { ["filter"] = null, ["limit"] = -4 }));
        await Assert.ThrowsAsync<EntityAIToolException>(() => Count(new() { ["filter"] = "{ not json" }));
        await Assert.ThrowsAsync<EntityAIToolException>(() => Count(new() { ["filter"] = 42 }));
    }
}
