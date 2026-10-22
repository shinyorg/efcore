using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Shiny.EntityFrameworkCore.Mcp.Implementation;

/// <summary>
/// The orientation resources. A model that can read <c>efcore://entities</c> and one schema stops guessing tool
/// arguments, which is worth more than any amount of tool-description prose.
/// </summary>
/// <remarks>
/// Every resource is a hand-written <see cref="AIFunction"/> with a hand-written schema, and every one that touches
/// data does so <b>through the registered tools</b>, so the scope, the page cap and the hidden fields apply to a
/// resource read exactly as they do to a tool call. A resource that queried the context directly would be a hole in
/// the scope.
/// </remarks>
static class McpResourceFactory
{
    public static IReadOnlyList<McpServerResource> Build(DbContextMcpPrimitives primitives) =>
    [
        McpServerResource.Create(new EntitiesResource(primitives), new McpServerResourceCreateOptions
        {
            UriTemplate = "efcore://entities",
            Name = "efcore-entities",
            Title = "Entities",
            Description = "Every entity this server exposes, with its capabilities, fields and tools.",
            MimeType = "application/json"
        }),
        McpServerResource.Create(new SchemaResource(primitives), new McpServerResourceCreateOptions
        {
            UriTemplate = "efcore://entities/{name}/schema",
            Name = "efcore-entity-schema",
            Title = "Entity schema",
            Description = "JSON Schema of one exposed entity, as the tools return it.",
            MimeType = "application/json"
        }),
        McpServerResource.Create(new SampleResource(primitives), new McpServerResourceCreateOptions
        {
            UriTemplate = "efcore://entities/{name}/sample",
            Name = "efcore-entity-sample",
            Title = "Entity sample",
            Description = "A few real rows of one exposed entity — the fastest way to learn its shape.",
            MimeType = "application/json"
        }),
        McpServerResource.Create(new StatsResource(primitives), new McpServerResourceCreateOptions
        {
            UriTemplate = "efcore://stats",
            Name = "efcore-stats",
            Title = "Statistics",
            Description = "Each context's provider, and per-entity row counts within the caller's scope.",
            MimeType = "application/json"
        })
    ];

    static bool Exposed(DbContextMcpBuilder options) => options.ResourcesExposed;

    abstract class ResourceFunction : AIFunction
    {
        static readonly JsonElement NoArguments = Schema("""{"type":"object","properties":{},"required":[]}""");
        static readonly JsonElement NameArgument = Schema(
            """{"type":"object","properties":{"name":{"type":"string","description":"The exposed entity name."}},"required":["name"]}""");

        protected ResourceFunction(DbContextMcpPrimitives primitives, bool takesName)
        {
            this.Primitives = primitives;
            this.JsonSchema = takesName ? NameArgument : NoArguments;
        }

        protected DbContextMcpPrimitives Primitives { get; }
        public override JsonElement JsonSchema { get; }

        static JsonElement Schema(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        protected McpEntity Entity(AIFunctionArguments arguments)
        {
            var slug = arguments.TryGetValue("name", out var raw)
                ? raw as string ?? (raw is JsonElement { ValueKind: JsonValueKind.String } je ? je.GetString() : null)
                : null;

            return (slug is null ? null : this.Primitives.Entity(slug, Exposed))
                ?? throw new ModelContextProtocol.McpException($"'{slug}' is not an entity exposed by this server.");
        }

        protected static string Json(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        protected static JsonNode? AsNode(object? result) => result as JsonNode ?? JsonNode.Parse(JsonSerializer.Serialize(result));
    }

    sealed class EntitiesResource(DbContextMcpPrimitives primitives) : ResourceFunction(primitives, takesName: false)
    {
        public override string Name => "efcore-entities";
        public override string Description => "Every entity this server exposes.";

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var array = new JsonArray();
            foreach (var entity in this.Primitives.Entities.Where(e => Exposed(e.Surface.Options)))
            {
                var d = entity.Descriptor;
                array.Add(new JsonObject
                {
                    ["name"] = d.Slug,
                    ["context"] = entity.Surface.ContextName,
                    ["description"] = d.Description,
                    ["capabilities"] = d.Capabilities.ToString(),
                    ["maxPageSize"] = d.MaxPageSize,
                    // The model is told a scope EXISTS (so "no results" is not a mystery) but never what it is.
                    ["serverScoped"] = d.IsScoped,
                    ["fields"] = new JsonArray(d.Fields.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
                    ["tools"] = new JsonArray(d.ToolNames.Select(t => (JsonNode)JsonValue.Create(t)).ToArray())
                });
            }
            return ValueTask.FromResult<object?>(Json(new JsonObject { ["entities"] = array }));
        }
    }

    sealed class SchemaResource(DbContextMcpPrimitives primitives) : ResourceFunction(primitives, takesName: true)
    {
        public override string Name => "efcore-entity-schema";
        public override string Description => "JSON Schema of one exposed entity.";

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var entity = this.Entity(arguments);
            return ValueTask.FromResult<object?>(Json(new JsonObject
            {
                ["name"] = entity.Descriptor.Slug,
                ["schema"] = JsonNode.Parse(entity.Descriptor.Schema.GetRawText())
            }));
        }
    }

    sealed class SampleResource(DbContextMcpPrimitives primitives) : ResourceFunction(primitives, takesName: true)
    {
        public override string Name => "efcore-entity-sample";
        public override string Description => "A few real rows of one exposed entity.";

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var entity = this.Entity(arguments);
            var slug = entity.Descriptor.Slug;
            var query = this.Primitives.Function($"{slug}_query")
                ?? throw new ModelContextProtocol.McpException($"'{slug}' is exposed without the Query capability, so it has no rows to sample.");

            // Straight through the query tool: same scope, same cap, same hidden fields. The call's Services travel
            // with it so a request-resolved scope resolves here too.
            var result = await query.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["limit"] = Math.Min(entity.Surface.Options.SampleSize, entity.Descriptor.MaxPageSize)
            })
            {
                Services = arguments.Services
            }, cancellationToken).ConfigureAwait(false);

            return Json(new JsonObject { ["name"] = slug, ["sample"] = AsNode(result) });
        }
    }

    sealed class StatsResource(DbContextMcpPrimitives primitives) : ResourceFunction(primitives, takesName: false)
    {
        public override string Name => "efcore-stats";
        public override string Description => "Each context's provider, and per-entity row counts within the caller's scope.";

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var contexts = new JsonArray();
            foreach (var surface in this.Primitives.Surfaces.Where(s => Exposed(s.Options)))
            {
                var counts = new JsonObject();
                foreach (var descriptor in surface.Descriptors)
                {
                    var count = this.Primitives.Function($"{descriptor.Slug}_count");
                    if (count != null)
                    {
                        // Counted through the tool, so a scoped entity reports the caller's slice — not a total that
                        // quietly tells the model how much it is not being shown.
                        var result = await count.InvokeAsync(new AIFunctionArguments { Services = arguments.Services }, cancellationToken).ConfigureAwait(false);
                        counts[descriptor.Slug] = AsNode(result)?["count"]?.DeepClone();
                    }
                }

                contexts.Add(new JsonObject
                {
                    ["context"] = surface.ContextName,
                    ["provider"] = surface.ProviderName,
                    ["counts"] = counts
                });
            }

            return Json(new JsonObject { ["contexts"] = contexts });
        }
    }
}
