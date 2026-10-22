using System.Text.Json;
using Shiny.EntityFrameworkCore.AI.Tests.Fixtures;
using Xunit;

namespace Shiny.EntityFrameworkCore.AI.Tests;

/// <summary>The schemas come from the EF model: types, nullability, keys, enums, lengths, comments and nesting.</summary>
public class SchemaTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    ToolHost Host(Action<IEntityAIToolBuilder<Order>>? configure = null) => new(database, b => b
        .AddEntity<Order>(t =>
        {
            t.AllowAll();
            configure?.Invoke(t);
        })
        .AddEntity<Customer>(t => t.AllowAll()));

    static JsonElement Document(ToolHost host, string tool)
        => host.Tool(tool).JsonSchema.GetProperty("properties").GetProperty("document");

    static JsonElement Properties(JsonElement schema) => schema.GetProperty("properties");

    static string[] FieldEnum(ToolHost host, string tool)
        => host.Tool(tool).JsonSchema.GetProperty("properties").GetProperty("filter")
            .GetProperty("$defs").GetProperty("filter").GetProperty("properties").GetProperty("field")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public async Task Fields_ComeFromTheModel_AndExcludeWhatTheFilterCannotExpress()
    {
        await using var host = this.Host();
        var fields = FieldEnum(host, "order_query");

        Assert.Equal(
            ["id", "customerId", "customerName", "notes", "placedAt", "quantity", "rating", "secret", "status", "tenantId", "total", "shipTo.city", "shipTo.zip"],
            fields);
        Assert.DoesNotContain("attachment", fields);   // byte[]
        Assert.DoesNotContain("auditTag", fields);     // shadow
        Assert.DoesNotContain("customer", fields);     // navigation
    }

    [Fact]
    public async Task Ignore_RemovesTheFieldFromEverySchema()
    {
        await using var host = this.Host(t => t.Ignore(o => o.Secret, o => o.ShipTo));

        foreach (var tool in host.Tools.Tools.Cast<Microsoft.Extensions.AI.AIFunction>())
        {
            var json = tool.JsonSchema.GetRawText();
            Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("shipTo", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AllowProperties_IsAnAllowList_IncludingNestedGroups()
    {
        await using var host = this.Host(t => t.AllowProperties(o => o.Id, o => o.CustomerName, o => o.ShipTo));
        Assert.Equal(["id", "customerName", "shipTo.city", "shipTo.zip"], FieldEnum(host, "order_query"));
    }

    [Fact]
    public async Task FieldSchemas_CarryTypeNullabilityEnumLengthAndDescription()
    {
        await using var host = this.Host(t => t.Property(o => o.Quantity, "Units ordered"));
        var props = Properties(Document(host, "order_update"));

        Assert.Equal("integer", props.GetProperty("quantity").GetProperty("type").GetString());
        Assert.Equal("Units ordered", props.GetProperty("quantity").GetProperty("description").GetString());

        Assert.Equal(["number", "null"], props.GetProperty("rating").GetProperty("type").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["Pending", "Shipped", "Delivered", "Cancelled"], props.GetProperty("status").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(100, props.GetProperty("customerName").GetProperty("maxLength").GetInt32());
        Assert.Equal("Customer display name", props.GetProperty("customerName").GetProperty("description").GetString());   // [Description]
        Assert.Equal("Order total in USD", props.GetProperty("total").GetProperty("description").GetString());            // HasComment
        Assert.Equal("date-time", props.GetProperty("placedAt").GetProperty("format").GetString());
        Assert.Equal("uuid", props.GetProperty("customerId").GetProperty("format").GetString());
    }

    [Fact]
    public async Task ComplexAndOwnedMembers_AreNestedObjects()
    {
        await using var host = this.Host();

        var shipTo = Properties(Document(host, "order_insert")).GetProperty("shipTo");
        Assert.Equal("string", Properties(shipTo).GetProperty("city").GetProperty("type").GetString());

        var contact = Properties(Document(host, "customer_insert")).GetProperty("contact");
        Assert.Equal(["string", "null"], Properties(contact).GetProperty("email").GetProperty("type").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Insert_OmitsAStoreGeneratedKey_ButRequiresAClientSuppliedOne()
    {
        await using var host = this.Host();

        Assert.False(Properties(Document(host, "order_insert")).TryGetProperty("id", out _));

        var customer = Document(host, "customer_insert");
        Assert.True(Properties(customer).TryGetProperty("id", out _));
        Assert.Contains("id", customer.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Update_RequiresTheKey()
    {
        await using var host = this.Host();
        var document = Document(host, "order_update");

        Assert.Equal(["id"], document.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("Primary key", Properties(document).GetProperty("id").GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdSchema_IsTypedLikeTheKey()
    {
        await using var host = this.Host();

        var orderId = host.Tool("order_get_by_id").JsonSchema.GetProperty("properties").GetProperty("id");
        Assert.Equal(["integer", "string"], orderId.GetProperty("type").EnumerateArray().Select(e => e.GetString()));

        var customerId = host.Tool("customer_delete").JsonSchema.GetProperty("properties").GetProperty("id");
        Assert.Equal("uuid", customerId.GetProperty("format").GetString());
    }

    [Fact]
    public async Task QuerySchema_CapsLimitAtMaxPageSize()
    {
        await using var host = this.Host(t => t.MaxPageSize(25));
        var limit = host.Tool("order_query").JsonSchema.GetProperty("properties").GetProperty("limit");
        Assert.Equal(25, limit.GetProperty("maximum").GetInt32());
    }

    [Fact]
    public async Task AggregateSchema_OffersOnlyAggregatableFields()
    {
        await using var host = this.Host();
        var fields = host.Tool("order_aggregate").JsonSchema.GetProperty("properties").GetProperty("field")
            .GetProperty("enum").EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray();

        Assert.Equal(["id", "placedAt", "quantity", "rating", "total"], fields);
    }

    [Fact]
    public async Task Descriptions_ComeFromTheClassOrTheTableComment_OrTheOverride()
    {
        await using (var host = this.Host())
        {
            Assert.Contains("A customer order", host.Tool("order_query").Description, StringComparison.Ordinal);
            Assert.Contains("People who buy things", host.Tool("customer_query").Description, StringComparison.Ordinal);
        }

        await using var overridden = this.Host(t => t.Description("Purchase orders"));
        Assert.Contains("Purchase orders", overridden.Tool("order_query").Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopePredicates_NeverAppearInSchemasOrDescriptions()
    {
        await using var host = this.Host(t => t.Where(o => o.CustomerName != "TopSecretTenantMarker"));
        foreach (var tool in host.Tools.Tools.Cast<Microsoft.Extensions.AI.AIFunction>())
        {
            Assert.DoesNotContain("TopSecretTenantMarker", tool.JsonSchema.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("TopSecretTenantMarker", tool.Description, StringComparison.Ordinal);
        }
    }
}
