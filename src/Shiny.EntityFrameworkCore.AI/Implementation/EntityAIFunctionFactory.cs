using Microsoft.Extensions.AI;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

static class EntityAIFunctionFactory
{
    public static IReadOnlyList<AIFunction> Build<T>(EntityAIBinding<T> binding, IDbContextSource source) where T : class
    {
        var caps = binding.Registration.Capabilities;
        var slug = binding.Registration.Slug;
        var noun = binding.Noun;
        var fields = binding.Fields;
        var tools = new List<AIFunction>();

        if (caps.HasFlag(EntityAICapabilities.Get))
            tools.Add(new GetByIdFunction<T>(binding, source,
                $"{slug}_get_by_id",
                $"Fetch a single {noun} by its '{binding.Key!.Name}'.",
                SchemaBuilder.BuildIdSchema(binding.Key, $"The record's '{binding.Key.Name}'.")));

        if (caps.HasFlag(EntityAICapabilities.Query))
            tools.Add(new QueryFunction<T>(binding, source,
                $"{slug}_query",
                $"Query {noun} with a structured filter, optional sort and paging (at most {binding.Registration.MaxPageSize} per call).",
                SchemaBuilder.BuildQuerySchema(fields, binding.Registration.MaxPageSize)));

        if (caps.HasFlag(EntityAICapabilities.Count))
            tools.Add(new CountFunction<T>(binding, source,
                $"{slug}_count",
                $"Count {noun}, optionally restricted by a structured filter.",
                SchemaBuilder.BuildCountSchema(fields)));

        if (caps.HasFlag(EntityAICapabilities.Aggregate))
            tools.Add(new AggregateFunction<T>(binding, source,
                $"{slug}_aggregate",
                $"Compute count/sum/min/max/avg over {noun}, optionally restricted by a structured filter.",
                SchemaBuilder.BuildAggregateSchema(fields)));

        if (caps.HasFlag(EntityAICapabilities.Insert))
            tools.Add(new InsertFunction<T>(binding, source,
                $"{slug}_insert",
                $"Insert a new {noun}.",
                SchemaBuilder.BuildInsertSchema(fields, binding.Description)));

        if (caps.HasFlag(EntityAICapabilities.Update))
            tools.Add(new UpdateFunction<T>(binding, source,
                $"{slug}_update",
                $"Update an existing {noun}: identify it by '{binding.Key!.Name}' and supply only the fields to change.",
                SchemaBuilder.BuildUpdateSchema(fields, binding.Key, binding.Description)));

        if (caps.HasFlag(EntityAICapabilities.Delete))
            tools.Add(new DeleteFunction<T>(binding, source,
                $"{slug}_delete",
                $"Delete a {noun} by its '{binding.Key!.Name}'.",
                SchemaBuilder.BuildIdSchema(binding.Key, $"The '{binding.Key.Name}' of the record to delete.")));

        return tools;
    }
}
