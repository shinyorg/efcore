using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Shiny.EntityFrameworkCore.AI.Implementation;

namespace Shiny.EntityFrameworkCore.AI;

/// <summary>
/// The <see cref="AITool"/>s generated for the entities registered against <typeparamref name="TContext"/>. Resolve it
/// from DI (after <c>AddDbContextAITools&lt;TContext&gt;</c>) or build it with <c>CreateAITools</c>, and hand
/// <see cref="Tools"/> to your <c>IChatClient</c> via <c>ChatOptions.Tools</c>.
/// </summary>
/// <remarks>
/// The tools are stateless and thread-safe: every invocation opens its own <typeparamref name="TContext"/> and disposes
/// it, so a model calling several tools in parallel never shares a context (and never trips EF's concurrent-use
/// guard), and nothing it touches lingers in a change tracker.
/// </remarks>
public sealed class DbContextAITools<TContext> where TContext : DbContext
{
    internal DbContextAITools(IReadOnlyList<AIFunction> functions, IReadOnlyList<EntityAIDescriptor> descriptors, string? providerName)
    {
        this.Functions = functions;
        this.Tools = functions.Cast<AITool>().ToArray();
        this.Descriptors = descriptors;
        this.ProviderName = providerName;
    }

    /// <summary>One tool per granted capability per registered entity.</summary>
    public IReadOnlyList<AITool> Tools { get; }

    internal IReadOnlyList<AIFunction> Functions { get; }

    /// <summary>What a host may say about each registration — never the scope, never a hidden field.</summary>
    internal IReadOnlyList<EntityAIDescriptor> Descriptors { get; }

    /// <summary>The EF provider (<c>Database.ProviderName</c>) the context was configured with.</summary>
    internal string? ProviderName { get; }
}
