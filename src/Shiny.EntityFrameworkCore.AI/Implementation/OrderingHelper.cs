using System.Linq.Expressions;
using System.Reflection;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>
/// Builds <c>OrderBy</c>/<c>ThenBy</c> calls with a correctly typed key selector (no <c>(object)</c> boxing for EF to
/// unpick), and always finishes on the primary key so a page boundary is deterministic — without that, "offset 50"
/// can repeat or skip rows between two calls, and SQL Server refuses OFFSET without ORDER BY anyway.
/// </summary>
static class OrderingHelper
{
    public static IQueryable<T> Apply<T>(IQueryable<T> query, EntityField? field, bool descending, IReadOnlyList<PropertyInfo>? key)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var ordered = false;

        if (field != null)
        {
            query = Order(query, field.Access(parameter), parameter, descending ? "OrderByDescending" : "OrderBy");
            ordered = true;
        }

        if (key != null)
        {
            foreach (var property in key)
            {
                var method = ordered ? "ThenBy" : "OrderBy";
                query = Order(query, Expression.Property(parameter, property), parameter, method);
                ordered = true;
            }
        }

        return query;
    }

    static IQueryable<T> Order<T>(IQueryable<T> query, Expression keyBody, ParameterExpression parameter, string method)
    {
        var call = Expression.Call(
            typeof(Queryable),
            method,
            [typeof(T), keyBody.Type],
            query.Expression,
            Expression.Quote(Expression.Lambda(keyBody, parameter)));

        return query.Provider.CreateQuery<T>(call);
    }
}
