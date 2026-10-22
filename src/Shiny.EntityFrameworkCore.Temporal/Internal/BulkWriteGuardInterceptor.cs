using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace Shiny.EntityFrameworkCore.Temporal.Internal;

/// <summary>
/// Rejects <c>ExecuteUpdate</c> / <c>ExecuteDelete</c> against entities with history. They run as a single set-based
/// statement that never materializes the rows, so there is nothing to snapshot and history would silently drift
/// from the table. The check runs at query compilation; because a rejected query is never compiled, it is never
/// cached either, so the guard holds on every call (raw SQL is out of its reach and is documented as such).
/// </summary>
sealed class BulkWriteGuardInterceptor : IQueryExpressionInterceptor
{
    public static readonly BulkWriteGuardInterceptor Instance = new();

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        if (queryExpression is MethodCallExpression { Method.Name: "ExecuteDelete" or "ExecuteUpdate" } call
            && eventData.Context is { } context
            && TemporalHistoryOptionsExtension.Find(context) is { AllowBulkWrites: false })
        {
            // The rows being written come from the source chain's root (the first argument all the way down);
            // entities that only appear in a filter subquery are read, not written, so they don't count.
            var source = call.Arguments[0];
            while (source is MethodCallExpression { Arguments.Count: > 0 } inner)
                source = inner.Arguments[0];

            if (source is EntityQueryRootExpression root
                && HistoryMapping.Find(HistoryMapping.OwnershipRoot((IEntityType)root.EntityType)) is { } mapping)
            {
                throw new InvalidOperationException(
                    $"{call.Method.Name} targets '{root.EntityType.DisplayName()}', which keeps history (history name '{mapping.Name}'). " +
                    "Set-based writes bypass the change tracker and can't be recorded. Load and modify the entities and call SaveChanges, " +
                    "or set TemporalHistoryOptions.AllowBulkWrites to accept writes that leave no history.");
            }
        }
        return queryExpression;
    }
}
