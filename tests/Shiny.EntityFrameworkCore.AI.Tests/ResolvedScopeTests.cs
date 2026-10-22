using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Shiny.EntityFrameworkCore.AI.Implementation;
using Shiny.EntityFrameworkCore.AI.Tests.Fixtures;
using Xunit;

namespace Shiny.EntityFrameworkCore.AI.Tests;

/// <summary>
/// The request-resolved scope: resolved per call from the call's services, AND-ed with the static scope, and — the part
/// that matters — failing <b>closed</b>. Each fail-closed test asserts no SQL ran at all, not merely that the answer
/// looked empty.
/// </summary>
public class ResolvedScopeTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    async Task<(ToolHost Host, string Tenant, string Other)> Seeded(Action<IEntityAIToolBuilder<Order>> configure, Action<IServiceCollection>? services = null)
    {
        var host = new ToolHost(database, b => b.AddEntity<Order>(t => configure(t.AllowAll())), services);
        var tenant = Data.NewTenant();
        var other = Data.NewTenant();
        var (mine, theirs) = Data.Orders(tenant, other);
        await host.Seed([.. mine, .. theirs]);
        host.Sql.Clear();
        return (host, tenant, other);
    }

    static Action<IEntityAIToolBuilder<Order>> ByTenant => t => t.Where<ITenantContext>((s, _) => o => o.TenantId == s.TenantId);

    [Fact]
    public async Task TheSameToolInstance_ReturnsEachCallersOwnRows()
    {
        var (host, tenant, other) = await this.Seeded(ByTenant);
        await using var _ = host;

        Assert.Equal(5, (await host.Invoke("order_count", tenant: tenant))["count"]!.GetValue<int>());
        Assert.Equal(2, (await host.Invoke("order_count", tenant: other))["count"]!.GetValue<int>());
        Assert.Equal(0, (await host.Invoke("order_count", tenant: "nobody"))["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task ResolvedScope_AndsWithTheStaticOne()
    {
        var (host, tenant, _) = await this.Seeded(t => ByTenant(t.Where(o => o.Status != OrderStatus.Cancelled)));
        await using var _ = host;

        Assert.Equal(4, (await host.Invoke("order_count", tenant: tenant))["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task AllFourOverloads_Resolve()
    {
        var (host, tenant, _) = await this.Seeded(t => t
            .Where(ctx => o => o.TenantId == ctx.GetRequiredService<ITenantContext>().TenantId)
            .Where(async ctx =>
            {
                await Task.Yield();
                return (Expression<Func<Order, bool>>)(o => o.Quantity > 1);
            })
            .Where<ITenantContext>((s, _) => o => o.TenantId == s.TenantId)
            .Where<ITenantContext>(async (s, ctx) =>
            {
                await Task.Delay(1, ctx.CancellationToken);
                var id = s.TenantId;
                return o => o.TenantId == id && o.Quantity < 5;
            }));
        await using var _ = host;

        Assert.Equal(3, (await host.Invoke("order_count", tenant: tenant))["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task NoServicesOnTheCall_FailsClosed()
    {
        var (host, _, _) = await this.Seeded(ByTenant);
        await using var _ = host;

        foreach (var tool in new[] { "order_query", "order_count", "order_aggregate" })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.InvokeWithoutServices(tool, new { function = "count" }));
            Assert.Contains("refused rather than run unscoped", ex.Message, StringComparison.Ordinal);
        }
        Assert.Empty(host.Sql.Commands);
    }

    [Fact]
    public async Task AThrowingScope_FailsClosed()
    {
        var (host, tenant, _) = await this.Seeded(t => t.Where((Func<EntityAIFilterContext, Expression<Func<Order, bool>>>)(_ => throw new UnauthorizedAccessException("no tenant"))));
        await using var _ = host;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Invoke("order_query", tenant: tenant));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Invoke("order_delete", new { id = 1 }, tenant));
        Assert.Empty(host.Sql.Commands);
    }

    [Fact]
    public async Task ANullScope_FailsClosed()
    {
        var (host, tenant, _) = await this.Seeded(t => t.Where((Func<EntityAIFilterContext, Expression<Func<Order, bool>>>)(_ => null!)));
        await using var _ = host;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Invoke("order_count", tenant: tenant));
        Assert.Contains("returned null", ex.Message, StringComparison.Ordinal);
        Assert.Empty(host.Sql.Commands);
    }

    [Fact]
    public async Task AServiceMissingAtCallTime_FailsClosed()
    {
        var (host, tenant, _) = await this.Seeded(t => t.Where(ctx =>
        {
            var missing = ctx.GetRequiredService<IComparable>();
            return o => o.TenantId == missing.ToString();
        }));
        await using var _ = host;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Invoke("order_count", tenant: tenant));
        Assert.Contains("refused rather than run unscoped", ex.Message, StringComparison.Ordinal);
        Assert.Empty(host.Sql.Commands);
    }

    [Fact]
    public async Task GetService_ReturnsNullForAnOptionalService_AndExposesTheArguments()
    {
        object? seen = "sentinel";
        object? argument = null;
        var (host, tenant, _) = await this.Seeded(t => t.Where(ctx =>
        {
            seen = ctx.GetService<IComparable>();
            argument = ctx.Arguments["limit"];
            return o => true;
        }));
        await using var _ = host;

        await host.Invoke("order_query", new { limit = 3 }, tenant);
        Assert.Null(seen);
        Assert.NotNull(argument);
    }

    [Fact]
    public async Task StaticOnlyScopes_NeedNoServices()
    {
        var host = new ToolHost(database, b => b.AddEntity<Order>(t => t.AllowCount().Where(o => o.TenantId == "fixed")));
        await using var _ = host;

        Assert.Equal(0, (await host.InvokeWithoutServices("order_count"))["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task OnInsert_StampsAHiddenScopeColumn_BeforeTheScopeCheck()
    {
        var (host, tenant, _) = await this.Seeded(t => ByTenant(t
            .Ignore(o => o.TenantId)
            .OnInsert((order, ctx) => order.TenantId = ctx.GetRequiredService<ITenantContext>().TenantId)));
        await using var _ = host;

        var inserted = await host.Invoke("order_insert", new { document = new { customerName = "Stamped" } }, tenant);
        Assert.Null(inserted["item"]!["tenantId"]);
        Assert.Equal(tenant, (await host.Find<Order>(inserted["item"]!["id"]!.GetValue<int>()))!.TenantId);
    }

    [Fact]
    public async Task OnInsert_CannotSmuggleARowOutOfScope()
    {
        var (host, tenant, other) = await this.Seeded(t => ByTenant(t.OnInsert((order, _) => order.TenantId = "elsewhere")));
        await using var _ = host;

        await Assert.ThrowsAsync<EntityAIToolException>(() => host.Invoke("order_insert", new { document = new { tenantId = tenant, customerName = "X" } }, tenant));
    }

    [Fact]
    public async Task OnInsert_WithoutCallServices_UsesTheToolsOwnScope()
    {
        string? seen = null;
        var host = new ToolHost(database, b => b.AddEntity<Order>(t => t
            .AllowInsert()
            .OnInsert((order, ctx) =>
            {
                seen = ctx.GetService<ITenantContext>()?.TenantId;
                order.TenantId = "stamped";
            })));
        await using var _ = host;

        await host.InvokeWithoutServices("order_insert", new { document = new { customerName = "NoServices" } });
        Assert.Equal("", seen);
    }

}
