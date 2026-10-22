using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Shiny.EntityFrameworkCore.AI.Implementation;

namespace Shiny.EntityFrameworkCore.Mcp.Implementation;

/// <summary>One exposed entity, with the context it belongs to and that context's MCP options.</summary>
sealed record McpEntity(DbContextMcpSurface Surface, EntityAIDescriptor Descriptor);

/// <summary>
/// Everything the server publishes, built once from the container: every context's tools (wrapped for audit and for
/// model-readable refusals), the orientation resources and the prompts.
/// </summary>
sealed class DbContextMcpPrimitives
{
    public DbContextMcpPrimitives(IEnumerable<DbContextMcpRegistration> registrations, IServiceProvider services, ILoggerFactory? loggerFactory = null)
    {
        this.Surfaces = registrations.Select(r => r.Build(services)).ToArray();

        var duplicate = this.Surfaces
            .SelectMany(s => s.Functions.Select(f => (s.ContextName, f.Name)))
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException(
                $"Tool '{duplicate.Key}' is produced by more than one registration ({string.Join(", ", duplicate.Select(d => d.ContextName))}). " +
                "Give one of the entities a distinct name in AddEntity<T>(name: …).");

        this.Functions = this.Surfaces.SelectMany(s => s.Functions).Select(f => (AIFunction)new ModelFacingErrors(f)).ToArray();
        this.Entities = this.Surfaces.SelectMany(s => s.Descriptors.Select(d => new McpEntity(s, d))).ToArray();

        var audit = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("Shiny.EntityFrameworkCore.Mcp.Tools");
        this.Tools = this.Functions
            .Select(fn => (McpServerTool)new AuditedMcpServerTool(McpServerTool.Create(fn), audit))
            .ToArray();

        this.Resources = this.Surfaces.Any(s => s.Options.ResourcesExposed)
            ? McpResourceFactory.Build(this)
            : Array.Empty<McpServerResource>();

        this.Prompts = this.Surfaces.Any(s => s.Options.PromptsExposed)
            ? McpPromptFactory.Build(this)
            : Array.Empty<McpServerPrompt>();
    }

    public IReadOnlyList<DbContextMcpSurface> Surfaces { get; }
    public IReadOnlyList<AIFunction> Functions { get; }
    public IReadOnlyList<McpEntity> Entities { get; }
    public IReadOnlyList<McpServerTool> Tools { get; }
    public IReadOnlyList<McpServerResource> Resources { get; }
    public IReadOnlyList<McpServerPrompt> Prompts { get; }

    /// <summary>The entity named <paramref name="slug"/>, if its context opted in to the given surface.</summary>
    public McpEntity? Entity(string slug, Func<DbContextMcpBuilder, bool> exposed)
        => this.Entities.FirstOrDefault(e => exposed(e.Surface.Options) && string.Equals(e.Descriptor.Slug, slug, StringComparison.Ordinal));

    /// <summary>The generated (wrapped) function with this exact name, or null when the capability was not granted.</summary>
    public AIFunction? Function(string name)
        => this.Functions.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Turns the tools' model-facing refusals into <see cref="McpException"/>, the one exception type whose message the
    /// SDK returns to the client. Everything else (a fail-closed scope, a provider error) still surfaces as the SDK's
    /// generic "an error occurred" — its text may describe the server, and the model has no business reading that.
    /// </summary>
    sealed class ModelFacingErrors(AIFunction inner) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            try
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (EntityAIToolException ex)
            {
                throw new McpException(ex.Message, ex);
            }
        }
    }
}
