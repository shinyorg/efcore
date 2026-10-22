using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shiny.EntityFrameworkCore.Mcp.Tests.Fixtures;
using Xunit;

namespace Shiny.EntityFrameworkCore.Mcp.Tests;

/// <summary>
/// End to end over the real SDK and the real Streamable-HTTP transport: tool listing, the two write locks, the
/// per-caller scope from the authenticated user, refusals the model can read, the audit line, resources and prompts.
/// </summary>
public class McpServerTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static JsonElement Json(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        return JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text).RootElement.Clone();
    }

    static string ErrorText(CallToolResult result)
    {
        Assert.True(result.IsError);
        return Assert.IsType<TextContentBlock>(result.Content[0]).Text;
    }

    static JsonElement Resource(ReadResourceResult result)
        => JsonDocument.Parse(Assert.IsType<TextResourceContents>(result.Contents[0]).Text).RootElement.Clone();

    static McpServerFixture Scoped(Action<IDbContextMcpBuilder>? extra = null) => new(mcp => mcp
        .WithDbContext<ShopDbContext>(db =>
        {
            extra?.Invoke(db);
            db.AddEntity<Order>(t =>
            {
                McpServerFixture.ScopeByTenantClaim(t);
                t.Ignore(o => o.Secret);
            });
        }));

    static async Task<IReadOnlyList<string>> ToolNames(McpClient client)
        => (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // ── Tool surface ────────────────────────────────────────────────────

    [Fact]
    public async Task ListsOneToolPerGrantedCapability_ReadOnlyByDefault()
    {
        await using var fixture = Scoped();
        await using var client = await fixture.Connect("acme");

        Assert.Equal(["order_aggregate", "order_count", "order_get_by_id", "order_query"], await ToolNames(client));
    }

    [Fact]
    public void AWriteCapabilityWithoutAllowWrites_FailsAtStartup()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddMcpServer()
            .WithDbContext<ShopDbContext>(db => db.AddEntity<Order>(t => t.AllowAll())));

        Assert.Contains("AllowWrites", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registration_IsValidatedEagerly()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMcpServer().WithDbContext<ShopDbContext>(_ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddMcpServer().WithDbContext<ShopDbContext>(db => db.ExposeResources(0)));

        // A request-resolved scope whose service is not registered.
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMcpServer()
            .WithDbContext<ShopDbContext>(db => db.AddEntity<Order>(t => t.Where<IComparable>((_, _) => o => true))));
        Assert.Contains("IComparable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowWrites_UnlocksTheWriteTools_AndWritesStayScoped()
    {
        await using var fixture = new McpServerFixture(mcp => mcp.WithDbContext<ShopDbContext>(db => db
            .AllowWrites()
            .AddEntity<Order>(t =>
            {
                McpServerFixture.ScopeByTenantClaim(t);
                t.AllowAll();
            })));
        await fixture.Seed();
        await using var acme = await fixture.Connect("acme");

        Assert.Equal(7, (await ToolNames(acme)).Count);

        var inserted = Json(await acme.CallToolAsync("order_insert",
            new Dictionary<string, object?> { ["document"] = new { tenantId = "acme", customer = "Dana", total = 12.5m } }, cancellationToken: Ct));
        Assert.True(inserted.GetProperty("inserted").GetBoolean());

        // Cross-tenant insert: refused, and the refusal text reaches the model.
        var refused = ErrorText(await acme.CallToolAsync("order_insert",
            new Dictionary<string, object?> { ["document"] = new { tenantId = "globex", customer = "Eve" } }, cancellationToken: Ct));
        Assert.Contains("access policy", refused, StringComparison.Ordinal);

        var bobId = await fixture.Query(db => db.Set<Order>().Where(o => o.Customer == "Bob").Select(o => o.Id).SingleAsync(Ct));
        var deleted = Json(await acme.CallToolAsync("order_delete", new Dictionary<string, object?> { ["id"] = bobId }, cancellationToken: Ct));
        Assert.False(deleted.GetProperty("deleted").GetBoolean());
        Assert.True(await fixture.Query(db => db.Set<Order>().AnyAsync(o => o.Id == bobId, Ct)));
    }

    // ── The per-caller scope ────────────────────────────────────────────

    [Fact]
    public async Task EachCaller_SeesOnlyTheirTenant_FromTheSameToolInstance()
    {
        await using var fixture = Scoped();
        await fixture.Seed();
        await using var acme = await fixture.Connect("acme");
        await using var globex = await fixture.Connect("globex");

        var acmeRows = Json(await acme.CallToolAsync("order_query", cancellationToken: Ct));
        Assert.Equal(2, acmeRows.GetProperty("count").GetInt32());

        var globexRows = Json(await globex.CallToolAsync("order_query", cancellationToken: Ct));
        Assert.Equal(1, globexRows.GetProperty("count").GetInt32());
        Assert.Equal("Bob", globexRows.GetProperty("items")[0].GetProperty("customer").GetString());

        Assert.Equal(25m, Json(await globex.CallToolAsync("order_aggregate",
            new Dictionary<string, object?> { ["function"] = "sum", ["field"] = "total" }, cancellationToken: Ct)).GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task AnOutOfScopeId_IsNotFound_NotForbidden()
    {
        await using var fixture = Scoped();
        await fixture.Seed();
        await using var acme = await fixture.Connect("acme");
        var bobId = await fixture.Query(db => db.Set<Order>().Where(o => o.Customer == "Bob").Select(o => o.Id).SingleAsync(Ct));

        var theirs = Json(await acme.CallToolAsync("order_get_by_id", new Dictionary<string, object?> { ["id"] = bobId }, cancellationToken: Ct));
        var missing = Json(await acme.CallToolAsync("order_get_by_id", new Dictionary<string, object?> { ["id"] = 99999 }, cancellationToken: Ct));

        Assert.False(theirs.GetProperty("found").GetBoolean());
        Assert.Equal(missing.GetRawText(), theirs.GetRawText());
    }

    [Fact]
    public async Task AnAnonymousCaller_FailsClosed_WithoutLeakingWhy()
    {
        await using var fixture = Scoped();
        await fixture.Seed();
        await using var anonymous = await fixture.Connect();

        var error = ErrorText(await anonymous.CallToolAsync("order_count", cancellationToken: Ct));

        // The SDK's generic text: the scope's own exception (and anything it says about the server) stays server-side.
        Assert.DoesNotContain("tenant", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.Logs.Lines, l => l.Category == "Shiny.EntityFrameworkCore.Mcp.Tools"
            && l.Message.Contains("by (anonymous)", StringComparison.Ordinal)
            && l.Message.Contains("outcome=failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModelFacingRefusals_ReachTheModel()
    {
        await using var fixture = Scoped();
        await using var acme = await fixture.Connect("acme");

        var unknown = ErrorText(await acme.CallToolAsync("order_query",
            new Dictionary<string, object?> { ["filter"] = new { field = "secret", op = "eq", value = "hunter2" } }, cancellationToken: Ct));
        Assert.Contains("unknown or not exposed", unknown, StringComparison.Ordinal);

        var orInjection = Json(await acme.CallToolAsync("order_count", new Dictionary<string, object?>
        {
            ["filter"] = new { or = new object[] { new { field = "tenantId", op = "eq", value = "globex" }, new { field = "customer", op = "ne", value = "" } } }
        }, cancellationToken: Ct));
        Assert.Equal(0, orInjection.GetProperty("count").GetInt32());   // nothing seeded in this fixture for acme
    }

    [Fact]
    public async Task EveryCall_IsAudited_WithADigestNotTheArguments()
    {
        await using var fixture = Scoped();
        await fixture.Seed();
        await using var acme = await fixture.Connect("acme");

        await acme.CallToolAsync("order_query",
            new Dictionary<string, object?> { ["filter"] = new { field = "customer", op = "eq", value = "Alice-Sensitive" } }, cancellationToken: Ct);

        var line = Assert.Single(fixture.Logs.Lines, l => l.Message.StartsWith("MCP tool order_query", StringComparison.Ordinal));
        Assert.Contains("by user-of-acme", line.Message, StringComparison.Ordinal);
        Assert.Contains("outcome=success", line.Message, StringComparison.Ordinal);
        Assert.Matches("args=[0-9a-f]{16}", line.Message);
        Assert.DoesNotContain("Alice-Sensitive", line.Message, StringComparison.Ordinal);
    }

    // ── Hidden fields ───────────────────────────────────────────────────

    [Fact]
    public async Task IgnoredFields_AppearInNoSchemaNoResultAndNoResource()
    {
        await using var fixture = Scoped(db => db.ExposeResources());
        await fixture.Seed();
        await using var acme = await fixture.Connect("acme");

        foreach (var tool in await acme.ListToolsAsync(cancellationToken: Ct))
            Assert.DoesNotContain("secret", JsonSerializer.Serialize(tool.ProtocolTool.InputSchema), StringComparison.OrdinalIgnoreCase);

        var rows = await acme.CallToolAsync("order_query", cancellationToken: Ct);
        Assert.DoesNotContain("hunter2", Assert.IsType<TextContentBlock>(rows.Content[0]).Text, StringComparison.Ordinal);

        var schema = await acme.ReadResourceAsync("efcore://entities/order/schema", cancellationToken: Ct);
        Assert.DoesNotContain("secret", Assert.IsType<TextResourceContents>(schema.Contents[0]).Text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Resources ───────────────────────────────────────────────────────

    [Fact]
    public async Task Resources_EntitiesSchemaSampleAndStats()
    {
        await using var fixture = Scoped(db => db.ExposeResources(sampleSize: 1));
        await fixture.Seed();
        await using var acme = await fixture.Connect("acme");

        var entities = Resource(await acme.ReadResourceAsync("efcore://entities", cancellationToken: Ct)).GetProperty("entities");
        var order = Assert.Single(entities.EnumerateArray());
        Assert.Equal("order", order.GetProperty("name").GetString());
        Assert.Equal("ShopDbContext", order.GetProperty("context").GetString());
        Assert.Equal("Customer orders", order.GetProperty("description").GetString());
        Assert.True(order.GetProperty("serverScoped").GetBoolean());
        Assert.Contains("order_query", order.GetProperty("tools").EnumerateArray().Select(t => t.GetString()));

        var schema = Resource(await acme.ReadResourceAsync("efcore://entities/order/schema", cancellationToken: Ct));
        Assert.Equal("Who placed it", schema.GetProperty("schema").GetProperty("properties").GetProperty("customer").GetProperty("description").GetString());

        var sample = Resource(await acme.ReadResourceAsync("efcore://entities/order/sample", cancellationToken: Ct));
        Assert.Equal(1, sample.GetProperty("sample").GetProperty("count").GetInt32());

        var stats = Resource(await acme.ReadResourceAsync("efcore://stats", cancellationToken: Ct)).GetProperty("contexts")[0];
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", stats.GetProperty("provider").GetString());
        Assert.Equal(2, stats.GetProperty("counts").GetProperty("order").GetInt32());   // the caller's slice, not 3
    }

    [Fact]
    public async Task TheSampleResource_ObeysThePerCallerScope()
    {
        await using var fixture = Scoped(db => db.ExposeResources(sampleSize: 10));
        await fixture.Seed();
        await using var globex = await fixture.Connect("globex");

        var sample = Resource(await globex.ReadResourceAsync("efcore://entities/order/sample", cancellationToken: Ct));

        // A resource that queried the context directly would have returned all three.
        Assert.Equal(1, sample.GetProperty("sample").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task UnknownResourceNames_AreRefused()
    {
        await using var fixture = Scoped(db => db.ExposeResources());
        await using var acme = await fixture.Connect("acme");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => acme.ReadResourceAsync("efcore://entities/secrets/schema", cancellationToken: Ct).AsTask());
        Assert.Contains("not an entity exposed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResourcesAndPrompts_AreAbsentUnlessExposed()
    {
        await using var fixture = Scoped();
        await using var client = await fixture.Connect("acme");

        Assert.Null(client.ServerCapabilities.Resources);
        Assert.Null(client.ServerCapabilities.Prompts);
    }

    // ── Prompts ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Prompts_DescribeTheEntity_WithoutLeakingTheScope()
    {
        await using var fixture = new McpServerFixture(mcp => mcp.WithDbContext<ShopDbContext>(db => db
            .ExposePrompts()
            .AddEntity<Order>(t => t.Where(o => o.TenantId == "acme-secret-scope"))));
        await using var client = await fixture.Connect("acme");

        var prompts = await client.ListPromptsAsync(cancellationToken: Ct);
        Assert.Equal(["build-filter", "explain-entity"], prompts.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        foreach (var name in new[] { "build-filter", "explain-entity" })
        {
            var prompt = await client.GetPromptAsync(name,
                new Dictionary<string, object?> { ["name"] = "order", ["ask"] = "big orders" }, cancellationToken: Ct);
            var text = string.Join("\n", prompt.Messages.Select(m => (m.Content as TextContentBlock)?.Text));

            Assert.Contains("order_query", text, StringComparison.Ordinal);
            Assert.Contains("total", text, StringComparison.Ordinal);
            Assert.Contains("row-level scope", text, StringComparison.Ordinal);
            Assert.DoesNotContain("acme-secret-scope", text, StringComparison.Ordinal);
        }
    }

    // ── Several contexts on one server ──────────────────────────────────

    [Fact]
    public async Task TwoContexts_ShareOneServer()
    {
        await using var fixture = new McpServerFixture(mcp => mcp
            .WithDbContext<ShopDbContext>(db => db.ExposeResources().AddEntity<Order>(McpServerFixture.ScopeByTenantClaim))
            .WithDbContext<NotesDbContext>(db => db.AddEntity<Note>(t => t.AllowCount())));
        await using var client = await fixture.Connect("acme");

        var names = await ToolNames(client);
        Assert.Contains("note_count", names);
        Assert.Contains("order_query", names);

        // Only the context that opted in to resources is listed in them.
        var entities = Resource(await client.ReadResourceAsync("efcore://entities", cancellationToken: Ct)).GetProperty("entities");
        Assert.Equal(["order"], entities.EnumerateArray().Select(e => e.GetProperty("name").GetString()));
    }

    [Fact]
    public void DuplicateToolNamesAcrossContexts_FailWhenTheServerStarts()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new McpServerFixture(mcp => mcp
            .WithDbContext<ShopDbContext>(db => db.AddEntity<Order>(name: "thing"))
            .WithDbContext<NotesDbContext>(db => db.AddEntity<Note>(name: "thing"))));

        Assert.Contains("'thing_get_by_id'", ex.Message, StringComparison.Ordinal);
    }
}
