using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Shiny.EntityFrameworkCore.Mcp.Implementation;

/// <summary>
/// The two prompts. Both are pure text built from what the server already exposes — they read no data, so they need
/// no scope, and neither mentions what the scope is.
/// </summary>
static class McpPromptFactory
{
    public static IReadOnlyList<McpServerPrompt> Build(DbContextMcpPrimitives primitives) =>
    [
        McpServerPrompt.Create(new ExplainEntityPrompt(primitives), new McpServerPromptCreateOptions
        {
            Name = "explain-entity",
            Title = "Explain an entity",
            Description = "Describe what lives in an exposed entity and how to filter it."
        }),
        McpServerPrompt.Create(new BuildFilterPrompt(primitives), new McpServerPromptCreateOptions
        {
            Name = "build-filter",
            Title = "Build a filter",
            Description = "Turn a natural-language ask into a query call for one exposed entity."
        })
    ];

    abstract class PromptFunction : AIFunction
    {
        protected PromptFunction(DbContextMcpPrimitives primitives, string schema)
        {
            this.Primitives = primitives;
            using var document = JsonDocument.Parse(schema);
            this.JsonSchema = document.RootElement.Clone();
        }

        protected DbContextMcpPrimitives Primitives { get; }
        public override JsonElement JsonSchema { get; }

        protected static string? Arg(AIFunctionArguments arguments, string key)
            => arguments.TryGetValue(key, out var raw)
                ? raw as string ?? (raw is JsonElement { ValueKind: JsonValueKind.String } je ? je.GetString() : null)
                : null;

        protected string Describe(string? slug)
        {
            var entity = (slug is null ? null : this.Primitives.Entity(slug, o => o.PromptsExposed))
                ?? throw new ModelContextProtocol.McpException($"'{slug}' is not an entity exposed by this server.");

            var d = entity.Descriptor;
            var sb = new StringBuilder();
            sb.Append("Entity: ").AppendLine(d.Slug);
            if (!string.IsNullOrWhiteSpace(d.Description))
                sb.Append("Description: ").AppendLine(d.Description);
            sb.Append("Fields: ").AppendLine(string.Join(", ", d.Fields));
            sb.Append("Tools: ").AppendLine(string.Join(", ", d.ToolNames));
            sb.Append("Max page size: ").Append(d.MaxPageSize).AppendLine();
            if (d.IsScoped)
                sb.AppendLine("Note: the server applies its own row-level scope. Some rows may not be visible; that is expected and cannot be overridden.");
            return sb.ToString();
        }
    }

    sealed class ExplainEntityPrompt(DbContextMcpPrimitives primitives) : PromptFunction(primitives,
        """{"type":"object","properties":{"name":{"type":"string","description":"The exposed entity name."}},"required":["name"]}""")
    {
        public override string Name => "explain-entity";
        public override string Description => "Describe what lives in an exposed entity and how to filter it.";

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var slug = Arg(arguments, "name");
            var description = this.Describe(slug);
            return ValueTask.FromResult<object?>(
                $"""
                 Describe the records available in this entity, using the tools listed below. If the server publishes
                 resources, read efcore://entities/{slug}/schema for the field types and efcore://entities/{slug}/sample
                 for a few real rows first, then summarise what the entity holds and which fields are worth filtering on.

                 {description}
                 """);
        }
    }

    sealed class BuildFilterPrompt(DbContextMcpPrimitives primitives) : PromptFunction(primitives,
        """{"type":"object","properties":{"name":{"type":"string","description":"The exposed entity name."},"ask":{"type":"string","description":"What the user wants to find, in their own words."}},"required":["name","ask"]}""")
    {
        public override string Name => "build-filter";
        public override string Description => "Turn a natural-language ask into a query call for one exposed entity.";

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var slug = Arg(arguments, "name");
            var description = this.Describe(slug);
            var ask = Arg(arguments, "ask") ?? "";

            // $$ so the JSON braces stay literal and only {{...}} interpolates.
            return ValueTask.FromResult<object?>(
                $$"""
                  Turn this request into a call to the '{{slug}}_query' tool:

                      {{ask}}

                  The tool takes a structured 'filter' argument — a single condition
                  { "field": "...", "op": "eq|ne|gt|gte|lt|lte|contains|startsWith|endsWith|in", "value": ... }
                  or exactly one boolean group { "and": [ ... ] } / { "or": [ ... ] } / { "not": { ... } } — plus
                  optional 'orderBy', 'orderDirection', 'limit' and 'offset'. Nested fields use dots (e.g. 'address.city').
                  Only the fields listed below may be named; anything else is refused. Prefer one precise filter over
                  fetching a page and reasoning over it.

                  {{description}}
                  """);
        }
    }
}
