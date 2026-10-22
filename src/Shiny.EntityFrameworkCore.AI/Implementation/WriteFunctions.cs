using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>
/// <c>{entity}_insert</c>: builds a new entity from the visible, writable fields the model supplied, runs the
/// <c>OnInsert</c> hooks, and refuses it unless the scope holds for it in memory — then saves through the context, so
/// every <c>SaveChanges</c> interceptor the app registered still runs.
/// </summary>
sealed class InsertFunction<T>(EntityAIBinding<T> binding, IDbContextSource source, string name, string description, JsonElement schema)
    : EntityAIFunctionBase<T>(binding, source, name, description, schema) where T : class
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var document = AIArguments.GetJson(arguments, "document")
            ?? throw new EntityAIToolException("'document' argument is required.");
        var (assignments, _) = EntityJson.ReadAssignments(document, this.Binding.FieldMap, f => f.InsertWritable, identity: null);

        var scope = await this.ResolveScope(arguments, cancellationToken).ConfigureAwait(false);
        await using var lease = await this.Source.OpenAsync(arguments.Services, cancellationToken).ConfigureAwait(false);

        var entity = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
        foreach (var (field, value) in assignments)
            field.SetValue(entity, value);

        var hooks = this.Binding.Registration.InsertHooks;
        if (hooks.Count > 0)
        {
            var services = arguments.Services ?? lease.Services
                ?? throw new InvalidOperationException(
                    "An OnInsert hook is configured, but this call carries no AIFunctionArguments.Services to run it with.");
            var context = new EntityAIFilterContext(services, arguments, cancellationToken);
            foreach (var hook in hooks)
                hook(entity, context);
        }

        // The scope is a hard boundary: the model cannot create a row it would not be allowed to see.
        if (!scope.Contains(entity))
            throw new EntityAIToolException("The record violates the access policy for this entity and was not inserted.");

        lease.Context.Set<T>().Add(entity);
        await Save(lease.Context, cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["inserted"] = true,
            ["item"] = EntityJson.FromEntity(this.Binding.Fields, entity)
        };
    }
}

/// <summary>
/// <c>{entity}_update</c>: a patch. The row is found <b>through</b> the scope (so an out-of-scope key is "not found"),
/// the supplied fields are applied to the tracked entity, and the scope must still hold afterwards (so the model
/// cannot move a row out of its own reach). Only the changed columns are written.
/// </summary>
sealed class UpdateFunction<T>(EntityAIBinding<T> binding, IDbContextSource source, string name, string description, JsonElement schema)
    : EntityAIFunctionBase<T>(binding, source, name, description, schema) where T : class
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var document = AIArguments.GetJson(arguments, "document")
            ?? throw new EntityAIToolException("'document' argument is required.");
        var key = this.Binding.Key!;
        var (assignments, id) = EntityJson.ReadAssignments(document, this.Binding.FieldMap, f => f.UpdateWritable, identity: key);
        if (id is null)
            throw new EntityAIToolException($"'document' must include the record's '{key.Name}'.");

        var scope = await this.ResolveScope(arguments, cancellationToken).ConfigureAwait(false);
        await using var lease = await this.Source.OpenAsync(arguments.Services, cancellationToken).ConfigureAwait(false);

        var entity = await Scoped(lease.Context, scope, tracking: true)
            .FirstOrDefaultAsync(this.KeyEquals(id), cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
            return new JsonObject { ["updated"] = false };

        foreach (var (field, value) in assignments)
            field.SetValue(entity, value);

        if (!scope.Contains(entity))
            throw new EntityAIToolException("The change would move the record outside the access policy for this entity and was not saved.");

        await Save(lease.Context, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["updated"] = true,
            ["item"] = EntityJson.FromEntity(this.Binding.Fields, entity)
        };
    }
}

/// <summary>
/// <c>{entity}_delete</c>: the row is found through the scope and removed through the change tracker (not
/// <c>ExecuteDelete</c>), so soft-delete, audit, outbox and temporal interceptors still see it.
/// </summary>
sealed class DeleteFunction<T>(EntityAIBinding<T> binding, IDbContextSource source, string name, string description, JsonElement schema)
    : EntityAIFunctionBase<T>(binding, source, name, description, schema) where T : class
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var id = this.ReadId(arguments);
        var scope = await this.ResolveScope(arguments, cancellationToken).ConfigureAwait(false);
        await using var lease = await this.Source.OpenAsync(arguments.Services, cancellationToken).ConfigureAwait(false);

        var entity = await Scoped(lease.Context, scope, tracking: true)
            .FirstOrDefaultAsync(this.KeyEquals(id), cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
            return new JsonObject { ["deleted"] = false };

        lease.Context.Set<T>().Remove(entity);
        await Save(lease.Context, cancellationToken).ConfigureAwait(false);
        return new JsonObject { ["deleted"] = true };
    }
}
