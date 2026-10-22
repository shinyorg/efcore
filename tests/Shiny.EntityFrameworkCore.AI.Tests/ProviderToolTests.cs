using System.Text.Json.Nodes;
using Shiny.EntityFrameworkCore.AI.Implementation;
using Shiny.EntityFrameworkCore.AI.Tests.Fixtures;
using Xunit;

namespace Shiny.EntityFrameworkCore.AI.Tests;

/// <summary>
/// Every capability, run against a real provider, with a request-resolved tenant scope. Each test owns a fresh tenant
/// and seeds a second tenant's rows next to it, so "the scope held" is proven by those rows never appearing — and the
/// captured SQL proves it held <b>in the database</b>, not in a post-filter.
/// </summary>
public abstract class ProviderToolTests(DatabaseFixture database)
{
    protected abstract string PagingKeyword { get; }

    protected ToolHost Host(Action<IEntityAIToolBuilder<Order>>? extra = null) => new(database, b => b
        .AddEntity<Order>(t =>
        {
            t.AllowAll()
                .Where<ITenantContext>((tenant, _) => o => o.TenantId == tenant.TenantId)
                .Ignore(o => o.Secret);
            extra?.Invoke(t);
        })
        .AddEntity<Customer>(t => t
            .AllowAll()
            .Where<ITenantContext>((tenant, _) => c => c.TenantId == tenant.TenantId)));

    async Task<(ToolHost Host, string Tenant, Order[] Mine, Order[] Theirs)> Seeded(Action<IEntityAIToolBuilder<Order>>? extra = null)
    {
        var host = this.Host(extra);
        var tenant = Data.NewTenant();
        var (mine, theirs) = Data.Orders(tenant, Data.NewTenant());
        await host.Seed([.. mine, .. theirs]);
        host.Sql.Clear();
        return (host, tenant, mine, theirs);
    }

    static string[] Names(JsonNode result)
        => result["items"]!.AsArray().Select(i => i!["customerName"]!.GetValue<string>()).ToArray();

    // ── Query ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Query_ReturnsOnlyTheScopedRows_AndTheScopeIsInTheSql()
    {
        var (host, tenant, _, theirs) = await this.Seeded();
        await using var _ = host;

        var result = await host.Invoke("order_query", tenant: tenant);

        Assert.Equal(5, result["count"]!.GetValue<int>());
        Assert.DoesNotContain("Mallory", Names(result));

        var sql = Assert.Single(host.Sql.Commands);
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TenantId", sql, StringComparison.Ordinal);
        Assert.Contains(this.PagingKeyword, sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tenant, sql, StringComparison.Ordinal);            // a parameter, not a literal
        Assert.DoesNotContain(theirs[0].TenantId, sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_NeverSelectsAnIgnoredColumn()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;

        var result = await host.Invoke("order_query", tenant: tenant);

        Assert.DoesNotContain("Secret", host.Sql.Last, StringComparison.Ordinal);
        Assert.DoesNotContain("AuditTag", host.Sql.Last, StringComparison.Ordinal);
        Assert.All(result["items"]!.AsArray(), item => Assert.Null(item!["secret"]));
    }

    [Fact]
    public async Task Query_FilterSortAndPage()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;

        var shipped = await host.Invoke("order_query", new
        {
            filter = new { field = "status", op = "eq", value = "Shipped" },
            orderBy = "total",
            orderDirection = "desc"
        }, tenant);
        Assert.Equal(["Carol", "Bob"], Names(shipped));

        var page = await host.Invoke("order_query", new { orderBy = "quantity", limit = 2, offset = 1 }, tenant);
        Assert.Equal(["Bob", "Carol"], Names(page));
        Assert.Equal(2, page["limit"]!.GetValue<int>());
        Assert.Equal(1, page["offset"]!.GetValue<int>());
    }

    [Fact]
    public async Task Query_FilterValuesAreParameters()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;

        await host.Invoke("order_query", new { filter = new { field = "customerName", op = "eq", value = "Carol" } }, tenant);

        Assert.DoesNotContain("Carol", host.Sql.Last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_LimitIsCappedByMaxPageSize()
    {
        var (host, tenant, _, _) = await this.Seeded(t => t.MaxPageSize(2));
        await using var _ = host;

        var result = await host.Invoke("order_query", new { limit = 500 }, tenant);

        Assert.Equal(2, result["count"]!.GetValue<int>());
        Assert.Equal(2, result["limit"]!.GetValue<int>());
    }

    [Fact]
    public async Task Query_StringOperators()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;

        Assert.Equal(["Carol"], Names(await host.Invoke("order_query", new { filter = new { field = "customerName", op = "contains", value = "aro" } }, tenant)));
        Assert.Equal(["Dave"], Names(await host.Invoke("order_query", new { filter = new { field = "customerName", op = "startsWith", value = "Da" } }, tenant)));
        Assert.Equal(["Erin"], Names(await host.Invoke("order_query", new { filter = new { field = "customerName", op = "endsWith", value = "rin" } }, tenant)));
        Assert.Equal(["Dave", "Erin"], Names(await host.Invoke("order_query", new { filter = new { field = "customerName", op = "gt", value = "Carol" }, orderBy = "customerName" }, tenant)));
    }

    [Fact]
    public async Task Query_InNotAndNull()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;

        Assert.Equal(["Alice", "Erin"], Names(await host.Invoke("order_query",
            new { filter = new { field = "status", op = "in", value = new[] { "Pending", "Cancelled" } } }, tenant)));

        Assert.Equal(4, (await host.Invoke("order_query",
            new { filter = new { not = new { field = "customerName", op = "eq", value = "Bob" } } }, tenant))["count"]!.GetValue<int>());

        Assert.Equal(["Carol"], Names(await host.Invoke("order_query",
            new { filter = new { field = "rating", op = "eq", value = (double?)null } }, tenant)));

        Assert.Equal(["Carol"], Names(await host.Invoke("order_query",
            new { filter = new { field = "notes", op = "ne", value = (string?)null } }, tenant)));
    }

    [Fact]
    public async Task Query_DateAndNumberComparisons()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;

        var result = await host.Invoke("order_query", new
        {
            filter = new
            {
                and = new object[]
                {
                    new { field = "placedAt", op = "gte", value = "2026-01-03T00:00:00Z" },
                    new { field = "quantity", op = "lt", value = 5 }
                }
            }
        }, tenant);

        Assert.Equal(["Carol", "Dave"], Names(result));
    }

    [Fact]
    public async Task Query_FiltersOnAComplexTypeMember()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;

        // Two Toronto rows belong to the other tenant; the scope keeps them out.
        var result = await host.Invoke("order_query", new { filter = new { field = "shipTo.city", op = "eq", value = "Toronto" } }, tenant);

        Assert.Equal(["Alice", "Carol", "Erin"], Names(result));
        Assert.Equal("Toronto", result["items"]![0]!["shipTo"]!["city"]!.GetValue<string>());
    }

    [Fact]
    public async Task Query_AnOrInTheModelFilterCannotWidenTheScope()
    {
        var (host, tenant, _, theirs) = await this.Seeded();
        await using var _ = host;

        var result = await host.Invoke("order_query", new
        {
            filter = new
            {
                or = new object[]
                {
                    new { field = "tenantId", op = "eq", value = theirs[0].TenantId },
                    new { field = "customerName", op = "ne", value = "" }
                }
            }
        }, tenant);

        Assert.Equal(5, result["count"]!.GetValue<int>());
        Assert.DoesNotContain("Mallory", Names(result));
    }

    // ── Count / aggregate ───────────────────────────────────────────────

    [Fact]
    public async Task Count_IsScopedAndFiltered()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;

        Assert.Equal(5, (await host.Invoke("order_count", tenant: tenant))["count"]!.GetValue<int>());
        Assert.Equal(2, (await host.Invoke("order_count", new { filter = new { field = "status", op = "eq", value = "Shipped" } }, tenant))["count"]!.GetValue<int>());
        Assert.Contains("COUNT", host.Sql.Last, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TenantId", host.Sql.Last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aggregate_AllFunctions_AreScoped()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;

        async Task<JsonNode?> Agg(string function, string? field = null, object? filter = null)
            => (await host.Invoke("order_aggregate", new { function, field, filter }, tenant))["value"];

        Assert.Equal(5, (await Agg("count"))!.GetValue<int>());
        Assert.Equal(15, (await Agg("sum", "quantity"))!.GetValue<int>());
        Assert.Equal(1, (await Agg("min", "quantity"))!.GetValue<int>());
        Assert.Equal(5, (await Agg("max", "quantity"))!.GetValue<int>());
        Assert.Equal(3.0, (await Agg("avg", "quantity"))!.GetValue<double>());
        Assert.Equal(150.50m, (await Agg("sum", "total"))!.GetValue<decimal>());
        Assert.Equal(50m, (await Agg("max", "total"))!.GetValue<decimal>());
        Assert.Equal(3.375, (await Agg("avg", "rating"))!.GetValue<double>(), 3);
        Assert.Equal(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), DateTime.Parse((await Agg("max", "placedAt"))!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind));
        Assert.Contains("TenantId", host.Sql.Last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aggregate_OverAnEmptySet_IsNullOrZero_NotAnError()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;
        var none = new { field = "customerName", op = "eq", value = "nobody" };

        Assert.Null((await host.Invoke("order_aggregate", new { function = "min", field = "quantity", filter = none }, tenant))["value"]);
        Assert.Null((await host.Invoke("order_aggregate", new { function = "avg", field = "total", filter = none }, tenant))["value"]);
        Assert.Equal(0, (await host.Invoke("order_aggregate", new { function = "sum", field = "quantity", filter = none }, tenant))["value"]!.GetValue<int>());
    }

    // ── Get / insert / update / delete ──────────────────────────────────

    [Fact]
    public async Task GetById_OutOfScopeIsIndistinguishableFromMissing()
    {
        var (host, tenant, mine, theirs) = await this.Seeded();
        await using var _ = host;

        var found = await host.Invoke("order_get_by_id", new { id = mine[1].Id }, tenant);
        Assert.True(found["found"]!.GetValue<bool>());
        Assert.Equal("Bob", found["item"]!["customerName"]!.GetValue<string>());
        Assert.Null(found["item"]!["secret"]);

        var outOfScope = await host.Invoke("order_get_by_id", new { id = theirs[0].Id.ToString() }, tenant);
        var missing = await host.Invoke("order_get_by_id", new { id = int.MaxValue }, tenant);
        Assert.Equal(missing.ToJsonString(), outOfScope.ToJsonString());
        Assert.Contains("TenantId", host.Sql.Last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Insert_InScope_IsSaved_OutOfScope_IsRefused()
    {
        var (host, tenant, _, theirs) = await this.Seeded();
        await using var _ = host;

        var inserted = await host.Invoke("order_insert", new
        {
            document = new { tenantId = tenant, customerName = "Frank", status = "Pending", total = 12.25m, quantity = 7, placedAt = "2026-02-01T00:00:00Z", shipTo = new { city = "Halifax" } }
        }, tenant);
        var id = inserted["item"]!["id"]!.GetValue<int>();
        Assert.True(id > 0);
        Assert.Equal("Halifax", (await host.Find<Order>(id))!.ShipTo.City);

        var ex = await Assert.ThrowsAsync<EntityAIToolException>(() => host.Invoke("order_insert", new
        {
            document = new { tenantId = theirs[0].TenantId, customerName = "Sneaky", placedAt = "2026-02-01T00:00:00Z", shipTo = new { city = "X" } }
        }, tenant));
        Assert.Contains("access policy", ex.Message, StringComparison.Ordinal);
        Assert.Equal(6, (await host.Invoke("order_count", tenant: tenant))["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Update_PatchesInScope_RefusesOutOfScope_AndCannotMoveARowOut()
    {
        var (host, tenant, mine, theirs) = await this.Seeded();
        await using var _ = host;

        var updated = await host.Invoke("order_update", new { document = new { id = mine[0].Id, status = "Delivered", notes = "left at door" } }, tenant);
        Assert.True(updated["updated"]!.GetValue<bool>());
        var stored = (await host.Find<Order>(mine[0].Id))!;
        Assert.Equal(OrderStatus.Delivered, stored.Status);
        Assert.Equal("left at door", stored.Notes);
        Assert.Equal("Alice", stored.CustomerName);       // omitted fields keep their values
        Assert.Equal("s1", stored.Secret);                // hidden fields are untouched

        var outOfScope = await host.Invoke("order_update", new { document = new { id = theirs[0].Id, customerName = "Hijacked" } }, tenant);
        Assert.False(outOfScope["updated"]!.GetValue<bool>());
        Assert.Equal("Mallory", (await host.Find<Order>(theirs[0].Id))!.CustomerName);

        await Assert.ThrowsAsync<EntityAIToolException>(() =>
            host.Invoke("order_update", new { document = new { id = mine[1].Id, tenantId = theirs[0].TenantId } }, tenant));
        Assert.Equal(tenant, (await host.Find<Order>(mine[1].Id))!.TenantId);
    }

    [Fact]
    public async Task Delete_InScope_Removes_OutOfScope_IsNotFound()
    {
        var (host, tenant, mine, theirs) = await this.Seeded();
        await using var _ = host;

        Assert.True((await host.Invoke("order_delete", new { id = mine[4].Id }, tenant))["deleted"]!.GetValue<bool>());
        Assert.Null(await host.Find<Order>(mine[4].Id));

        Assert.False((await host.Invoke("order_delete", new { id = theirs[1].Id }, tenant))["deleted"]!.GetValue<bool>());
        Assert.NotNull(await host.Find<Order>(theirs[1].Id));
    }

    [Fact]
    public async Task OwnedType_AndClientSuppliedGuidKey_RoundTrip()
    {
        await using var host = this.Host();
        var tenant = Data.NewTenant();
        var id = Guid.NewGuid();

        await host.Invoke("customer_insert", new
        {
            document = new { id, tenantId = tenant, name = "Zed", age = 41, contact = new { email = "zed@example.com" } }
        }, tenant);

        var byEmail = await host.Invoke("customer_query", new { filter = new { field = "contact.email", op = "eq", value = "zed@example.com" } }, tenant);
        Assert.Equal(1, byEmail["count"]!.GetValue<int>());
        Assert.Equal(id.ToString(), byEmail["items"]![0]!["id"]!.GetValue<string>());

        var got = await host.Invoke("customer_get_by_id", new { id = id.ToString() }, tenant);
        Assert.Equal("zed@example.com", got["item"]!["contact"]!["email"]!.GetValue<string>());

        Assert.False((await host.Invoke("customer_get_by_id", new { id = id.ToString() }, Data.NewTenant()))["found"]!.GetValue<bool>());
    }
}

public sealed class SqliteProviderToolTests(SqliteDatabase database) : ProviderToolTests(database), IClassFixture<SqliteDatabase>
{
    protected override string PagingKeyword => "LIMIT";
}

[Collection("postgres")]
public sealed class PostgresProviderToolTests(PostgresDatabase database) : ProviderToolTests(database)
{
    protected override string PagingKeyword => "LIMIT";
}

[Collection("sqlserver")]
public sealed class SqlServerProviderToolTests(SqlServerDatabase database) : ProviderToolTests(database)
{
    protected override string PagingKeyword => "OFFSET";
}

[Collection("mysql")]
public sealed class MySqlProviderToolTests(MySqlDatabase database) : ProviderToolTests(database)
{
    protected override string PagingKeyword => "LIMIT";
}
