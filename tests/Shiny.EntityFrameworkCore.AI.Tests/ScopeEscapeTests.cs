using Shiny.EntityFrameworkCore.AI.Implementation;
using Shiny.EntityFrameworkCore.AI.Tests.Fixtures;
using Xunit;

namespace Shiny.EntityFrameworkCore.AI.Tests;

/// <summary>
/// The adversarial suite: everything a hostile or confused model might send to see or touch a row outside its scope,
/// or a field it was never shown. Every attempt must either be refused outright or come back empty — and a refusal
/// must not distinguish "hidden" from "does not exist".
/// </summary>
public class ScopeEscapeTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    async Task<(ToolHost Host, string Tenant, Order[] Mine, Order[] Theirs)> Seeded()
    {
        var host = new ToolHost(database, b => b.AddEntity<Order>(t => t
            .AllowAll()
            .Where<ITenantContext>((tenant, _) => o => o.TenantId == tenant.TenantId)
            .Ignore(o => o.Secret)));

        var tenant = Data.NewTenant();
        var (mine, theirs) = Data.Orders(tenant, Data.NewTenant());
        await host.Seed([.. mine, .. theirs]);
        return (host, tenant, mine, theirs);
    }

    static Task<EntityAIToolException> Refused(Func<Task> call) => Assert.ThrowsAsync<EntityAIToolException>(call);

    [Fact]
    public async Task NotAroundTheWholeFilter_DoesNotInvertTheScope()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;

        // NOT(name == "nobody") is true for every row — including other tenants' rows, were the scope inside the NOT.
        var result = await host.Invoke("order_count", new { filter = new { not = new { field = "customerName", op = "eq", value = "nobody" } } }, tenant);
        Assert.Equal(5, result["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task NamingTheOtherTenantDirectly_FindsNothing()
    {
        var (host, tenant, _, theirs) = await this.Seeded();
        await using var _ = host;

        var result = await host.Invoke("order_query", new { filter = new { field = "tenantId", op = "eq", value = theirs[0].TenantId } }, tenant);
        Assert.Equal(0, result["count"]!.GetValue<int>());

        var sum = await host.Invoke("order_aggregate", new { function = "sum", field = "total", filter = new { field = "tenantId", op = "ne", value = tenant } }, tenant);
        Assert.Equal(0m, sum["value"]!.GetValue<decimal>());
    }

    [Theory]
    [InlineData("secret")]          // hidden with Ignore
    [InlineData("Secret")]          // CLR casing
    [InlineData("auditTag")]        // shadow property
    [InlineData("customer")]        // navigation
    [InlineData("customer.name")]   // through a navigation
    [InlineData("attachment")]      // unsupported type
    [InlineData("doesNotExist")]
    [InlineData("TenantId) OR 1=1 --")]
    public async Task UnknownOrHiddenFields_AreRefusedEverywhere_WithOneMessage(string field)
    {
        var (host, tenant, mine, _) = await this.Seeded();
        await using var _ = host;

        var messages = new[]
        {
            (await Refused(() => host.Invoke("order_query", new { filter = new { field, op = "eq", value = "x" } }, tenant))).Message,
            (await Refused(() => host.Invoke("order_query", new { orderBy = field }, tenant))).Message,
            (await Refused(() => host.Invoke("order_count", new { filter = new { field, op = "ne", value = "x" } }, tenant))).Message,
            (await Refused(() => host.Invoke("order_aggregate", new { function = "max", field }, tenant))).Message,
            (await Refused(() => host.Invoke("order_update", new { document = new Dictionary<string, object> { ["id"] = mine[0].Id, [field] = "x" } }, tenant))).Message,
            (await Refused(() => host.Invoke("order_insert", new { document = new Dictionary<string, object> { ["tenantId"] = tenant, [field] = "x" } }, tenant))).Message
        };

        Assert.All(messages, m => Assert.Contains("unknown or not exposed", m, StringComparison.Ordinal));
        Assert.Single(messages.Distinct());
    }

    [Fact]
    public async Task UpdateCannotWriteAHiddenField_AndTheHiddenValueSurvives()
    {
        var (host, tenant, mine, _) = await this.Seeded();
        await using var _ = host;

        await Refused(() => host.Invoke("order_update", new { document = new { id = mine[0].Id, secret = "leaked" } }, tenant));
        Assert.Equal("s1", (await host.Find<Order>(mine[0].Id))!.Secret);
    }

    [Fact]
    public async Task UpdateCannotChangeTheKey()
    {
        var (host, tenant, mine, _) = await this.Seeded();
        await using var _ = host;

        // The id in the body identifies the row; it is never a value to write. Re-pointing it at another row only
        // selects that row (through the scope).
        var result = await host.Invoke("order_update", new { document = new { id = mine[0].Id, customerName = "Renamed" } }, tenant);
        Assert.Equal(mine[0].Id, result["item"]!["id"]!.GetValue<int>());

        await Refused(() => host.Invoke("order_update", new { document = new { customerName = "No id" } }, tenant));
    }

    [Fact]
    public async Task InsertCannotSupplyAStoreGeneratedKey()
    {
        var (host, tenant, mine, _) = await this.Seeded();
        await using var _ = host;

        var ex = await Refused(() => host.Invoke("order_insert", new { document = new { id = mine[0].Id, tenantId = tenant } }, tenant));
        Assert.Contains("read-only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutOfScopeIds_AreNotFound_ForGetUpdateAndDelete()
    {
        var (host, tenant, _, theirs) = await this.Seeded();
        await using var _ = host;
        var id = theirs[0].Id;

        Assert.False((await host.Invoke("order_get_by_id", new { id }, tenant))["found"]!.GetValue<bool>());
        Assert.False((await host.Invoke("order_update", new { document = new { id, notes = "x" } }, tenant))["updated"]!.GetValue<bool>());
        Assert.False((await host.Invoke("order_delete", new { id }, tenant))["deleted"]!.GetValue<bool>());

        var stored = await host.Find<Order>(id);
        Assert.NotNull(stored);
        Assert.Null(stored.Notes);
    }

    [Fact]
    public async Task UpdateThatWouldLeaveTheScope_IsRefused_AndNothingIsWritten()
    {
        var (host, tenant, mine, theirs) = await this.Seeded();
        await using var _ = host;

        var ex = await Refused(() => host.Invoke("order_update",
            new { document = new { id = mine[2].Id, tenantId = theirs[0].TenantId, notes = "moved" } }, tenant));

        Assert.Contains("access policy", ex.Message, StringComparison.Ordinal);
        var stored = (await host.Find<Order>(mine[2].Id))!;
        Assert.Equal(tenant, stored.TenantId);
        Assert.Equal("rush", stored.Notes);
    }

    [Fact]
    public async Task InsertOutsideTheScope_IsRefused_AndNothingIsWritten()
    {
        var (host, tenant, _, theirs) = await this.Seeded();
        await using var _ = host;

        await Refused(() => host.Invoke("order_insert", new { document = new { tenantId = theirs[0].TenantId, customerName = "Planted" } }, tenant));
        await Refused(() => host.Invoke("order_insert", new { document = new { customerName = "No tenant at all" } }, tenant));

        await using var context = database.CreateContext();
        Assert.DoesNotContain(context.Orders, o => o.CustomerName == "Planted" || o.CustomerName == "No tenant at all");
    }

    [Fact]
    public async Task HostileFilterShapes_AreRefused()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;

        // Mixed node: a leaf AND an "or" — refusing beats guessing which half was meant.
        await Refused(() => host.Invoke("order_query", new { filter = new { field = "quantity", op = "gt", value = 1, or = new object[] { new { field = "quantity", op = "lt", value = 9 } } } }, tenant));

        // Deep nesting.
        object deep = new { field = "quantity", op = "gt", value = 0 };
        for (var i = 0; i < 40; i++)
            deep = new { not = deep };
        Assert.Contains("nested", (await Refused(() => host.Invoke("order_query", new { filter = deep }, tenant))).Message, StringComparison.Ordinal);

        // Too many conditions.
        var wide = Enumerable.Range(0, 200).Select(i => (object)new { field = "quantity", op = "ne", value = i }).ToArray();
        Assert.Contains("conditions", (await Refused(() => host.Invoke("order_count", new { filter = new { and = wide } }, tenant))).Message, StringComparison.Ordinal);

        // An oversized "in".
        Assert.Contains("at most", (await Refused(() => host.Invoke("order_count", new { filter = new { field = "quantity", op = "in", value = Enumerable.Range(0, 500) } }, tenant))).Message, StringComparison.Ordinal);

        // Unknown operator, wrong types, empty combinators.
        await Refused(() => host.Invoke("order_query", new { filter = new { field = "quantity", op = "like", value = "%" } }, tenant));
        await Refused(() => host.Invoke("order_query", new { filter = new { field = "quantity", op = "eq", value = "not a number" } }, tenant));
        await Refused(() => host.Invoke("order_query", new { filter = new { field = "quantity", op = "contains", value = "1" } }, tenant));
        await Refused(() => host.Invoke("order_query", new { filter = new { field = "status", op = "gt", value = "Pending" } }, tenant));
        await Refused(() => host.Invoke("order_query", new { filter = new { field = "status", op = "eq", value = "Teleported" } }, tenant));
        await Refused(() => host.Invoke("order_query", new { filter = new { field = "quantity", op = "eq", value = (int?)null } }, tenant));
        await Refused(() => host.Invoke("order_query", new { filter = new { field = "notes", op = "gt", value = (string?)null } }, tenant));
        await Refused(() => host.Invoke("order_query", new { filter = new { and = Array.Empty<object>() } }, tenant));
        await Refused(() => host.Invoke("order_query", new { filter = new { op = "eq", value = 1 } }, tenant));
        await Refused(() => host.Invoke("order_query", new { filter = "[1,2]" }, tenant));
    }

    [Fact]
    public async Task ArgumentErrors_AreCleanToolErrors()
    {
        var (host, tenant, _, _) = await this.Seeded();
        await using var _ = host;

        await Refused(() => host.Invoke("order_get_by_id", new { }, tenant));
        Assert.Contains("not a valid", (await Refused(() => host.Invoke("order_get_by_id", new { id = "abc" }, tenant))).Message, StringComparison.Ordinal);
        await Refused(() => host.Invoke("order_aggregate", new { }, tenant));
        await Refused(() => host.Invoke("order_aggregate", new { function = "median", field = "total" }, tenant));
        await Refused(() => host.Invoke("order_aggregate", new { function = "sum" }, tenant));
        await Refused(() => host.Invoke("order_aggregate", new { function = "sum", field = "customerName" }, tenant));
        await Refused(() => host.Invoke("order_aggregate", new { function = "avg", field = "placedAt" }, tenant));
        await Refused(() => host.Invoke("order_insert", new { }, tenant));
        await Refused(() => host.Invoke("order_insert", new { document = new[] { 1 } }, tenant));
        await Refused(() => host.Invoke("order_update", new { }, tenant));
    }

    [Fact]
    public async Task ADatabaseRejection_DoesNotLeakProviderDetail()
    {
        var host = new ToolHost(database, b => b.AddEntity<Customer>(t => t.AllowInsert()));
        await using var _ = host;
        var id = Guid.NewGuid();
        await host.Seed(new Customer { Id = id, TenantId = "a", Name = "First" });

        var ex = await Refused(() => host.Invoke("customer_insert", new { document = new { id, tenantId = "a", name = "Dup" } }));
        Assert.Equal("The database rejected the change.", ex.Message);
    }
}
