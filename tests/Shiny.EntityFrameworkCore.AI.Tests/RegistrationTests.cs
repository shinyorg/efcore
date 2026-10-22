using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shiny.EntityFrameworkCore.AI.Tests.Fixtures;
using Xunit;

namespace Shiny.EntityFrameworkCore.AI.Tests;

/// <summary>Which tools exist, and every configuration mistake that must fail at startup rather than at 3am.</summary>
public class RegistrationTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    ToolHost Host(Action<IDbContextAIToolBuilder> configure, Action<IServiceCollection>? services = null) => new(database, configure, services);

    [Fact]
    public async Task NoAllowCall_MeansReadOnly()
    {
        await using var host = this.Host(b => b.AddEntity<Order>());
        Assert.Equal(["order_aggregate", "order_count", "order_get_by_id", "order_query"], host.ToolNames);
    }

    [Fact]
    public async Task EachCapability_ProducesExactlyItsTool()
    {
        var cases = new (Func<IEntityAIToolBuilder<Order>, IEntityAIToolBuilder<Order>> Allow, string Tool)[]
        {
            (t => t.AllowGet(), "order_get_by_id"),
            (t => t.AllowQuery(), "order_query"),
            (t => t.AllowCount(), "order_count"),
            (t => t.AllowAggregate(), "order_aggregate"),
            (t => t.AllowInsert(), "order_insert"),
            (t => t.AllowUpdate(), "order_update"),
            (t => t.AllowDelete(), "order_delete")
        };

        foreach (var (allow, tool) in cases)
        {
            await using var host = this.Host(b => b.AddEntity<Order>(t => allow(t)));
            Assert.Equal([tool], host.ToolNames);
        }
    }

    [Fact]
    public async Task AllowAll_AndAllowFlags_Combine()
    {
        await using (var all = this.Host(b => b.AddEntity<Order>(t => t.AllowAll())))
            Assert.Equal(7, all.ToolNames.Count);

        await using var some = this.Host(b => b.AddEntity<Order>(t => t.Allow(EntityAICapabilities.Count | EntityAICapabilities.Delete).AllowReadOnly()));
        Assert.Equal(["order_aggregate", "order_count", "order_delete", "order_get_by_id", "order_query"], some.ToolNames);
    }

    [Fact]
    public async Task UnregisteredEntities_ProduceNoTools_AndNamesAreSlugged()
    {
        await using var host = this.Host(b => b
            .AddEntity<Order>(t => t.AllowQuery())
            .AddEntity<OrderTag>(t => t.AllowQuery())
            .AddEntity<Customer>(t => t.AllowQuery(), name: "people"));

        Assert.Equal(["order_query", "order_tag_query", "people_query"], host.ToolNames);
    }

    [Fact]
    public void EmptyRegistration_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDbContextAITools<TestDbContext>(_ => { }));
        Assert.Contains("at least one AddEntity", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateSlug_Throws()
        => Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDbContextAITools<TestDbContext>(b => b
            .AddEntity<Order>()
            .AddEntity<Customer>(name: "order")));

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("a_name_that_is_far_too_long_to_be_a_sensible_tool_prefix")]
    public void InvalidSlug_Throws(string name)
        => Assert.Throws<ArgumentException>(() => new ServiceCollection().AddDbContextAITools<TestDbContext>(b => b.AddEntity<Order>(name: name)));

    [Fact]
    public void ResolvedScope_WithUnregisteredService_FailsAtRegistration()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDbContextAITools<TestDbContext>(b => b
            .AddEntity<Order>(t => t.Where<IDisposable>((_, _) => o => true))));
        Assert.Contains("IDisposable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuilderArguments_AreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddDbContextAITools<TestDbContext>(b => b.AddEntity<Order>(t => t.MaxPageSize(0))));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddDbContextAITools<TestDbContext>(b => b.AddEntity<Order>(t => t.Ignore(o => o.ShipTo.City.Length + 1))));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddDbContextAITools<TestDbContext>(b => b.AddEntity<Order>(t => t.Description(" "))));
    }

    // ── Errors that need the EF model surface when the tools are first built ──

    static string BindError(DatabaseFixture database, Action<IDbContextAIToolBuilder> configure)
        => Assert.Throws<InvalidOperationException>(() => new ToolHost(database, configure)).Message;

    [Fact]
    public void AnEntityTheContextDoesNotMap_Throws()
        => Assert.Contains("not an entity type", BindError(database, b => b.AddEntity<ITenantContext>()), StringComparison.Ordinal);

    [Fact]
    public void AnOwnedType_Throws()
        => Assert.Contains("owned type", BindError(database, b => b.AddEntity<ContactInfo>()), StringComparison.Ordinal);

    [Fact]
    public void IdBasedTools_OnAKeylessEntity_Throw()
        => Assert.Contains("no single visible primary-key", BindError(database, b => b.AddEntity<SalesSummary>(t => t.AllowGet())), StringComparison.Ordinal);

    [Fact]
    public void IdBasedTools_OnACompositeKey_Throw()
        => Assert.Contains("no single visible primary-key", BindError(database, b => b.AddEntity<OrderTag>(t => t.AllowDelete())), StringComparison.Ordinal);

    [Fact]
    public void IdBasedTools_WithTheKeyHidden_Throw()
        => Assert.Contains("no single visible primary-key", BindError(database, b => b.AddEntity<Order>(t => t.AllowUpdate().Ignore(o => o.Id))), StringComparison.Ordinal);

    [Fact]
    public void Insert_OnAKeylessEntity_Throws()
        => Assert.Contains("keyless", BindError(database, b => b.AddEntity<SalesSummary>(t => t.AllowInsert())), StringComparison.Ordinal);

    [Fact]
    public void Insert_WithoutAParameterlessConstructor_Throws()
        => Assert.Contains("parameterless constructor", BindError(database, b => b.AddEntity<Invoice>(t => t.AllowInsert())), StringComparison.Ordinal);

    [Fact]
    public async Task KeylessAndCompositeEntities_AreFineForQueries()
    {
        await using var host = new ToolHost(database, b => b
            .AddEntity<SalesSummary>(t => t.AllowCount())
            .AddEntity<OrderTag>(t => t.AllowQuery()));
        Assert.Equal(["order_tag_query", "sales_summary_count"], host.ToolNames);
    }

    [Fact]
    public void AllowProperties_NamingANavigation_Throws()
        => Assert.Contains("not an exposable field", BindError(database, b => b.AddEntity<Order>(t => t.AllowProperties(o => o.Customer))), StringComparison.Ordinal);

    [Fact]
    public void PropertyDescription_ForAnExcludedType_Throws()
        => Assert.Contains("not an exposable field", BindError(database, b => b.AddEntity<Order>(t => t.Property(o => o.Attachment, "blob"))), StringComparison.Ordinal);

    [Fact]
    public void HidingEveryField_Throws()
        => Assert.Contains("exposes no fields", BindError(database, b => b.AddEntity<OrderTag>(t => t.Ignore(x => x.OrderId, x => x.Tag))), StringComparison.Ordinal);

    [Fact]
    public async Task Ignore_IsLenientAboutThingsAlreadyHidden()
    {
        await using var host = new ToolHost(database, b => b.AddEntity<Order>(t => t.Ignore(o => o.Customer, o => o.Attachment)));
        Assert.NotEmpty(host.ToolNames);
    }

    // ── Container-free path ──────────────────────────────────────────────

    sealed class Factory(DatabaseFixture database) : IDbContextFactory<TestDbContext>
    {
        public int Created { get; private set; }

        public TestDbContext CreateDbContext()
        {
            this.Created++;
            return database.CreateContext();
        }
    }

    [Fact]
    public async Task CreateAITools_FromAFactory_OpensOneContextPerCall()
    {
        var factory = new Factory(database);
        var tools = factory.CreateAITools(b => b.AddEntity<Order>(t => t.AllowCount().Where(o => o.TenantId == "nobody")));
        var created = factory.Created;

        var count = (Microsoft.Extensions.AI.AIFunction)tools.Tools.Single();
        await count.InvokeAsync(new(), TestContext.Current.CancellationToken);
        await count.InvokeAsync(new(), TestContext.Current.CancellationToken);

        Assert.Equal(created + 2, factory.Created);
    }

    [Fact]
    public void CreateAITools_RequiresARegistration()
        => Assert.Throws<InvalidOperationException>(() => new Factory(database).CreateAITools(_ => { }));
}
