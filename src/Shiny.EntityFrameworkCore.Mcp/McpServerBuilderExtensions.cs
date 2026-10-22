using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Shiny.EntityFrameworkCore.Mcp.Implementation;

namespace Shiny.EntityFrameworkCore.Mcp;

/// <summary>Adds a <c>DbContext</c>'s scoped AI tools (and optionally resources and prompts) to an MCP server.</summary>
public static class McpServerBuilderExtensions
{
    /// <summary>
    /// Publishes the entities of <typeparamref name="TContext"/> you opt in to as MCP tools on this server. Compose it
    /// with the SDK as usual — <c>services.AddMcpServer().WithHttpTransport().WithDbContext&lt;AppDbContext&gt;(…)</c>,
    /// then <c>app.MapMcp("/mcp")</c> — or with <c>.WithStdioServerTransport()</c> for a local desktop client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call it once per context; several contexts can share one server as long as their entity names (tool prefixes)
    /// are unique, which is checked when the server starts.
    /// </para>
    /// <para>
    /// On the HTTP transport the SDK runs each tool call in the request's DI scope and hands it to the tool, so a
    /// request-resolved <c>Where&lt;TService&gt;</c> scope sees the caller's identity (via <c>IHttpContextAccessor</c>
    /// or your own scoped user/tenant service). Each call still opens its own <typeparamref name="TContext"/>.
    /// </para>
    /// </remarks>
    public static IMcpServerBuilder WithDbContext<TContext>(this IMcpServerBuilder builder, Action<IDbContextMcpBuilder> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var mcp = new DbContextMcpBuilder();
        configure(mcp);
        mcp.Validate(builder.Services, typeof(TContext).Name);

        builder.Services.AddSingleton<DbContextMcpRegistration>(new DbContextMcpRegistration<TContext>(mcp));
        builder.Services.TryAddSingleton<DbContextMcpPrimitives>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<McpServerOptions>, DbContextMcpOptionsSetup>());
        return builder;
    }
}
