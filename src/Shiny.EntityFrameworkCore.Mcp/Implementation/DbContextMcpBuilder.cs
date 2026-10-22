using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shiny.EntityFrameworkCore.AI;
using Shiny.EntityFrameworkCore.AI.Implementation;

namespace Shiny.EntityFrameworkCore.Mcp.Implementation;

/// <summary>
/// The MCP builder: a thin shell over the AI tool builder that adds the MCP-only knobs. Registration is delegated
/// verbatim, so a fix to the tool surface reaches both lanes.
/// </summary>
sealed class DbContextMcpBuilder : IDbContextMcpBuilder
{
    public DbContextAIToolBuilder Inner { get; } = new();
    public bool WritesAllowed { get; private set; }
    public bool ResourcesExposed { get; private set; }
    public bool PromptsExposed { get; private set; }
    public int SampleSize { get; private set; } = 3;

    public IDbContextMcpBuilder AllowWrites()
    {
        this.WritesAllowed = true;
        return this;
    }

    public IDbContextMcpBuilder ExposeResources(int sampleSize = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleSize);
        this.ResourcesExposed = true;
        this.SampleSize = sampleSize;
        return this;
    }

    public IDbContextMcpBuilder ExposePrompts()
    {
        this.PromptsExposed = true;
        return this;
    }

    public IDbContextAIToolBuilder AddEntity<T>(Action<IEntityAIToolBuilder<T>>? configure = null, string? name = null) where T : class
        => this.Inner.AddEntity(configure, name);

    /// <summary>
    /// Startup validation: something must be registered, writes need the second lock, and every request-resolved
    /// scope's service must be registered.
    /// </summary>
    public void Validate(IServiceCollection services, string contextName)
    {
        this.Inner.ThrowIfEmpty($"WithDbContext<{contextName}>");

        if (!this.WritesAllowed)
        {
            foreach (var registration in this.Inner.Registrations.Values)
            {
                var writes = registration.Capabilities & EntityAICapabilities.Write;
                if (writes != EntityAICapabilities.None)
                    throw new InvalidOperationException(
                        $"'{registration.Slug}' asks for {writes}, but WithDbContext<{contextName}> has not called AllowWrites(). " +
                        "Writes need both locks — the per-entity capability and the server-wide switch — so an agent cannot " +
                        "be handed a destructive tool by a one-word config change.");
            }
        }

        this.Inner.ValidateRequiredServices(services, $"WithDbContext<{contextName}>");
    }
}

/// <summary>One <c>WithDbContext&lt;TContext&gt;</c> call, waiting for a container to build its tools from.</summary>
abstract class DbContextMcpRegistration(DbContextMcpBuilder builder)
{
    public DbContextMcpBuilder Builder { get; } = builder;

    public abstract DbContextMcpSurface Build(IServiceProvider services);
}

sealed class DbContextMcpRegistration<TContext>(DbContextMcpBuilder builder) : DbContextMcpRegistration(builder) where TContext : DbContext
{
    public override DbContextMcpSurface Build(IServiceProvider services)
    {
        var tools = DbContextAIToolsFactory.Build<TContext>(new ServiceProviderDbContextSource<TContext>(services), this.Builder.Inner);
        return new DbContextMcpSurface(typeof(TContext).Name, tools.ProviderName, tools.Functions, tools.Descriptors, this.Builder);
    }
}

/// <summary>What one context contributes to the server.</summary>
sealed record DbContextMcpSurface(
    string ContextName,
    string? ProviderName,
    IReadOnlyList<AIFunction> Functions,
    IReadOnlyList<EntityAIDescriptor> Descriptors,
    DbContextMcpBuilder Options
);
