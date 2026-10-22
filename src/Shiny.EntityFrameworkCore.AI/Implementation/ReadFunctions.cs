using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary><c>{entity}_query</c>: scope AND filter, ordered, capped page, visible columns only — one SQL statement.</summary>
sealed class QueryFunction<T>(EntityAIBinding<T> binding, IDbContextSource source, string name, string description, JsonElement schema)
    : EntityAIFunctionBase<T>(binding, source, name, description, schema) where T : class
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var maxPageSize = this.Binding.Registration.MaxPageSize;
        var limit = AIArguments.GetInt(arguments, "limit", Math.Min(50, maxPageSize));
        var offset = Math.Max(0, AIArguments.GetInt(arguments, "offset", 0));
        if (limit <= 0)
            limit = Math.Min(50, maxPageSize);
        limit = Math.Min(limit, maxPageSize);

        // Everything the model sent is validated before a connection is opened.
        var predicate = FilterTranslator.Translate<T>(AIArguments.GetJson(arguments, "filter"), this.Binding.FieldMap);
        var orderByName = AIArguments.GetString(arguments, "orderBy");
        var orderBy = string.IsNullOrWhiteSpace(orderByName) ? null : FilterTranslator.Resolve(this.Binding.FieldMap, orderByName);
        var descending = string.Equals(AIArguments.GetString(arguments, "orderDirection"), "desc", StringComparison.OrdinalIgnoreCase);

        var scope = await this.ResolveScope(arguments, cancellationToken).ConfigureAwait(false);
        await using var lease = await this.Source.OpenAsync(arguments.Services, cancellationToken).ConfigureAwait(false);

        var query = Scoped(lease.Context, scope, tracking: false);
        if (predicate != null)
            query = query.Where(predicate);

        var rows = await OrderingHelper.Apply(query, orderBy, descending, this.Binding.KeyProperties)
            .Skip(offset)
            .Take(limit)
            .Select(this.Binding.Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = new JsonArray();
        foreach (var row in rows)
            items.Add(EntityJson.FromValues(this.Binding.Fields, row));

        return new JsonObject
        {
            ["count"] = rows.Count,
            ["offset"] = offset,
            ["limit"] = limit,
            ["items"] = items
        };
    }
}

/// <summary><c>{entity}_count</c>: <c>SELECT COUNT(*)</c> over scope AND filter.</summary>
sealed class CountFunction<T>(EntityAIBinding<T> binding, IDbContextSource source, string name, string description, JsonElement schema)
    : EntityAIFunctionBase<T>(binding, source, name, description, schema) where T : class
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var predicate = FilterTranslator.Translate<T>(AIArguments.GetJson(arguments, "filter"), this.Binding.FieldMap);
        var scope = await this.ResolveScope(arguments, cancellationToken).ConfigureAwait(false);
        await using var lease = await this.Source.OpenAsync(arguments.Services, cancellationToken).ConfigureAwait(false);

        var query = Scoped(lease.Context, scope, tracking: false);
        if (predicate != null)
            query = query.Where(predicate);

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        return new JsonObject { ["count"] = count };
    }
}

/// <summary>
/// <c>{entity}_get_by_id</c>: the key lookup runs <b>through</b> the scope, so an out-of-scope id and a missing id
/// produce the same answer — existence is never leaked.
/// </summary>
sealed class GetByIdFunction<T>(EntityAIBinding<T> binding, IDbContextSource source, string name, string description, JsonElement schema)
    : EntityAIFunctionBase<T>(binding, source, name, description, schema) where T : class
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var id = this.ReadId(arguments);
        var scope = await this.ResolveScope(arguments, cancellationToken).ConfigureAwait(false);
        await using var lease = await this.Source.OpenAsync(arguments.Services, cancellationToken).ConfigureAwait(false);

        var row = await Scoped(lease.Context, scope, tracking: false)
            .Where(this.KeyEquals(id))
            .Select(this.Binding.Projection)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? new JsonObject { ["found"] = false }
            : new JsonObject { ["found"] = true, ["item"] = EntityJson.FromValues(this.Binding.Fields, row) };
    }
}
