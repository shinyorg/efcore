using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>
/// <c>{entity}_aggregate</c>: count/sum/min/max/avg over scope AND filter, computed by the database. Selectors are
/// lifted to <c>Nullable&lt;T&gt;</c>, so an empty set yields <c>null</c> (min/max/avg) or <c>0</c> (sum) rather than
/// the "sequence contains no elements" exception a non-nullable aggregate throws.
/// </summary>
sealed class AggregateFunction<T>(EntityAIBinding<T> binding, IDbContextSource source, string name, string description, JsonElement schema)
    : EntityAIFunctionBase<T>(binding, source, name, description, schema) where T : class
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var function = AIArguments.GetString(arguments, "function")?.ToLowerInvariant()
            ?? throw new EntityAIToolException("'function' argument is required.");
        if (function is not ("count" or "sum" or "min" or "max" or "avg"))
            throw new EntityAIToolException("'function' must be one of count, sum, min, max, avg.");

        EntityField? field = null;
        if (function != "count")
        {
            var fieldName = AIArguments.GetString(arguments, "field");
            if (string.IsNullOrWhiteSpace(fieldName))
                throw new EntityAIToolException($"Aggregate '{function}' requires a 'field' argument.");

            field = FilterTranslator.Resolve(this.Binding.FieldMap, fieldName);
            var supported = function is "sum" or "avg" ? ScalarTypes.IsNumeric(field.ClrType) : ScalarTypes.IsOrderable(field.ClrType);
            if (!supported)
                throw new EntityAIToolException(
                    $"'{function}' is not supported on field '{field.Name}' — sum/avg need a numeric field, min/max a numeric or date/time field.");
        }

        var predicate = FilterTranslator.Translate<T>(AIArguments.GetJson(arguments, "filter"), this.Binding.FieldMap);
        var scope = await this.ResolveScope(arguments, cancellationToken).ConfigureAwait(false);
        await using var lease = await this.Source.OpenAsync(arguments.Services, cancellationToken).ConfigureAwait(false);

        var query = Scoped(lease.Context, scope, tracking: false);
        if (predicate != null)
            query = query.Where(predicate);

        if (field is null)
        {
            var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);
            return new JsonObject { ["function"] = "count", ["value"] = count };
        }

        var value = await Dispatch(query, field, function, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["function"] = function,
            ["field"] = field.Name,
            ["value"] = ScalarTypes.ToJson(value)
        };
    }

    static Task<object?> Dispatch(IQueryable<T> query, EntityField field, string function, CancellationToken ct)
    {
        var t = Nullable.GetUnderlyingType(field.ClrType) ?? field.ClrType;

        // Small integers aggregate as int, uint as long, ulong as decimal — the widest lossless type the providers agree on.
        if (t == typeof(int) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte) || t == typeof(ushort))
            return Numeric<int>(query, field, function, ct);
        if (t == typeof(long) || t == typeof(uint))
            return Numeric<long>(query, field, function, ct);
        if (t == typeof(decimal) || t == typeof(ulong))
            return Numeric<decimal>(query, field, function, ct);
        if (t == typeof(double))
            return Numeric<double>(query, field, function, ct);
        if (t == typeof(float))
            return Numeric<float>(query, field, function, ct);
        if (t == typeof(DateTime))
            return MinMax<DateTime>(query, field, function, ct);
        if (t == typeof(DateTimeOffset))
            return MinMax<DateTimeOffset>(query, field, function, ct);
        if (t == typeof(DateOnly))
            return MinMax<DateOnly>(query, field, function, ct);
        if (t == typeof(TimeOnly))
            return MinMax<TimeOnly>(query, field, function, ct);
        return MinMax<TimeSpan>(query, field, function, ct);
    }

    static async Task<object?> Numeric<TValue>(IQueryable<T> query, EntityField field, string function, CancellationToken ct) where TValue : struct
    {
        if (function is "min" or "max")
            return await MinMax<TValue>(query, field, function, ct).ConfigureAwait(false);

        var selector = Selector<TValue>(field);
        var sum = function == "sum";
        return selector switch
        {
            Expression<Func<T, int?>> s => sum ? (object?)await query.SumAsync(s, ct).ConfigureAwait(false) : await query.AverageAsync(s, ct).ConfigureAwait(false),
            Expression<Func<T, long?>> s => sum ? (object?)await query.SumAsync(s, ct).ConfigureAwait(false) : await query.AverageAsync(s, ct).ConfigureAwait(false),
            Expression<Func<T, decimal?>> s => sum ? (object?)await query.SumAsync(s, ct).ConfigureAwait(false) : await query.AverageAsync(s, ct).ConfigureAwait(false),
            Expression<Func<T, double?>> s => sum ? (object?)await query.SumAsync(s, ct).ConfigureAwait(false) : await query.AverageAsync(s, ct).ConfigureAwait(false),
            Expression<Func<T, float?>> s => sum ? (object?)await query.SumAsync(s, ct).ConfigureAwait(false) : await query.AverageAsync(s, ct).ConfigureAwait(false),
            _ => throw new EntityAIToolException($"'{function}' is not supported on field '{field.Name}'.")
        };
    }

    static async Task<object?> MinMax<TValue>(IQueryable<T> query, EntityField field, string function, CancellationToken ct) where TValue : struct
    {
        var projected = query.Select(Selector<TValue>(field));
        return function == "min"
            ? await projected.MinAsync(ct).ConfigureAwait(false)
            : await projected.MaxAsync(ct).ConfigureAwait(false);
    }

    static Expression<Func<T, TValue?>> Selector<TValue>(EntityField field) where TValue : struct
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        Expression body = field.Access(parameter);
        if (body.Type != typeof(TValue?))
            body = Expression.Convert(body, typeof(TValue?));
        return Expression.Lambda<Func<T, TValue?>>(body, parameter);
    }
}
