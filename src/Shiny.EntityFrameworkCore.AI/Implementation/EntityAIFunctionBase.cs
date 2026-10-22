using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>
/// Shared plumbing for one entity's tools: the binding (fields, key, projection, compiled static scope), where contexts
/// come from, and the per-call scope resolution every tool starts with.
/// </summary>
abstract class EntityAIFunctionBase<T> : AIFunction where T : class
{
    readonly string name;
    readonly string description;
    readonly JsonElement schema;

    protected EntityAIFunctionBase(EntityAIBinding<T> binding, IDbContextSource source, string name, string description, JsonElement schema)
    {
        this.Binding = binding;
        this.Source = source;
        this.name = name;
        this.description = description;
        this.schema = schema;
    }

    protected EntityAIBinding<T> Binding { get; }
    protected IDbContextSource Source { get; }

    public override string Name => this.name;
    public override string Description => this.description;
    public override JsonElement JsonSchema => this.schema;

    /// <summary>
    /// Resolves this call's scope: the static predicates (compiled once) plus any request-resolved ones, evaluated now.
    /// Call it <b>once</b> per invocation and use the result for both the query and the in-memory checks.
    /// </summary>
    protected ValueTask<EntityAIScope<T>> ResolveScope(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => EntityAIScopeResolver.Resolve(
            this.Binding.Registration.Filters,
            this.Binding.StaticPredicates,
            this.Binding.Registration.DynamicFilters,
            arguments,
            cancellationToken);

    /// <summary>The entity set with the scope AND-applied — the only way any tool reaches the table.</summary>
    protected static IQueryable<T> Scoped(DbContext context, EntityAIScope<T> scope, bool tracking)
    {
        var set = context.Set<T>();
        return scope.ApplyTo(tracking ? set : set.AsNoTracking());
    }

    /// <summary><c>x =&gt; x.Id == @id</c> over the binding's single key.</summary>
    protected Expression<Func<T, bool>> KeyEquals(object key)
    {
        var field = this.Binding.Key!;
        var parameter = Expression.Parameter(typeof(T), "x");
        return Expression.Lambda<Func<T, bool>>(
            Expression.Equal(field.Access(parameter), FilterTranslator.Parameter(key, field.ClrType)),
            parameter);
    }

    /// <summary>Reads and converts the <c>id</c> argument to the key's CLR type.</summary>
    protected object ReadId(AIFunctionArguments arguments)
    {
        var raw = AIArguments.GetString(arguments, "id");
        if (string.IsNullOrWhiteSpace(raw))
            throw new EntityAIToolException("'id' argument is required.");
        return ScalarTypes.FromString(raw, this.Binding.Key!.ClrType, "id");
    }

    /// <summary>
    /// Saves, turning a database rejection into a plain tool error. Provider messages name constraints, tables and
    /// sometimes values — none of which belong in a model's context.
    /// </summary>
    protected static async Task Save(DbContext context, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw new EntityAIToolException("The database rejected the change.", ex);
        }
    }
}
