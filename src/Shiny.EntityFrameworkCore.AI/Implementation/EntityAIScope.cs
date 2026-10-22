using System.Linq.Expressions;
using Microsoft.Extensions.AI;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>A request-resolved scope delegate, run once per tool call.</summary>
delegate ValueTask<Expression<Func<T, bool>>> DynamicEntityFilter<T>(EntityAIFilterContext context) where T : class;

/// <summary>
/// The non-removable predicate set for a single tool call, already resolved. It carries the expressions (applied to
/// the <see cref="IQueryable{T}"/>, so they become SQL) <b>and</b> their compiled delegates (for the in-memory checks
/// the write paths make), built from the very same expression objects so the two enforcement paths cannot drift.
/// </summary>
readonly struct EntityAIScope<T> where T : class
{
    readonly IReadOnlyList<Expression<Func<T, bool>>> expressions;
    readonly Func<T, bool>[] predicates;

    public EntityAIScope(IReadOnlyList<Expression<Func<T, bool>>> expressions, Func<T, bool>[] predicates)
    {
        this.expressions = expressions;
        this.predicates = predicates;
    }

    public bool IsEmpty => this.predicates is null || this.predicates.Length == 0;

    /// <summary>AND-applies the scope to a query. Applied before the model's filter, as separate <c>Where</c> calls,
    /// so no shape of the model's filter (an <c>or</c>, a <c>not</c>) can re-associate with it.</summary>
    public IQueryable<T> ApplyTo(IQueryable<T> query)
    {
        if (this.expressions != null)
        {
            foreach (var expression in this.expressions)
                query = query.Where(expression);
        }
        return query;
    }

    /// <summary>
    /// True when the entity is inside the scope, evaluated in memory. A predicate that throws (a null member, a
    /// navigation that is not loaded) counts as <b>outside</b> — fail closed.
    /// </summary>
    public bool Contains(T entity)
    {
        if (this.predicates != null)
        {
            foreach (var predicate in this.predicates)
            {
                if (!Evaluate(predicate, entity))
                    return false;
            }
        }
        return true;
    }

    static bool Evaluate(Func<T, bool> predicate, T entity)
    {
        try
        {
            return predicate(entity);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>The one place that decides what "fail closed" means.</summary>
static class EntityAIScopeResolver
{
    const string NoServicesMessage =
        "A request-resolved scope is configured for '{0}', but this tool call carries no AIFunctionArguments.Services " +
        "to resolve it from. The call is refused rather than run unscoped — set Services on the arguments (the MCP " +
        "server does this from the request scope).";

    public static async ValueTask<EntityAIScope<T>> Resolve<T>(
        IReadOnlyList<Expression<Func<T, bool>>> staticExpressions,
        Func<T, bool>[] staticPredicates,
        IReadOnlyList<DynamicEntityFilter<T>> dynamicFilters,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken) where T : class
    {
        if (dynamicFilters.Count == 0)
            return new EntityAIScope<T>(staticExpressions, staticPredicates);

        var services = arguments.Services
            ?? throw new InvalidOperationException(string.Format(NoServicesMessage, typeof(T).Name));

        var context = new EntityAIFilterContext(services, arguments, cancellationToken);
        var expressions = new List<Expression<Func<T, bool>>>(staticExpressions.Count + dynamicFilters.Count);
        expressions.AddRange(staticExpressions);

        var predicates = new List<Func<T, bool>>(staticPredicates.Length + dynamicFilters.Count);
        predicates.AddRange(staticPredicates);

        foreach (var filter in dynamicFilters)
        {
            // A throwing filter (or a missing service) propagates and fails the tool call. Never caught here: "run
            // without the predicate" is the exact outcome this feature exists to prevent.
            var expression = await filter(context).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"A request-resolved scope for '{typeof(T).Name}' returned null. Return a predicate (use x => false " +
                    "to deny everything) — null is not a valid scope.");

            expressions.Add(expression);
            predicates.Add(expression.Compile(preferInterpretation: true));
        }

        return new EntityAIScope<T>(expressions, predicates.ToArray());
    }
}
