using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>
/// Translates the structured filter JSON the model produces into an <c>Expression&lt;Func&lt;T, bool&gt;&gt;</c> that EF
/// turns into SQL. The model never writes SQL and never names a column: it names a field from a closed set, and every
/// value it supplies becomes a SQL <b>parameter</b>, never a literal.
/// </summary>
/// <remarks>
/// The translated predicate is applied <i>after</i> the scope, as its own <c>Where</c>, so an <c>or</c> in it ORs only
/// with itself — <c>scope AND (model filter)</c> is the shape of every query, whatever the model sends.
/// </remarks>
static class FilterTranslator
{
    /// <summary>Deeper than any real question; shallow enough that a hostile filter cannot exhaust the stack.</summary>
    public const int MaxDepth = 16;

    /// <summary>Total leaves + combinators in one filter.</summary>
    public const int MaxNodes = 128;

    /// <summary>Values in one <c>in</c> list.</summary>
    public const int MaxInValues = 100;

    static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
    static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    static readonly MethodInfo StringEndsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;

    public static Expression<Func<T, bool>>? Translate<T>(JsonElement? filter, IReadOnlyDictionary<string, EntityField> fields)
    {
        if (filter is null || filter.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        var parameter = Expression.Parameter(typeof(T), "x");
        var nodes = 0;
        var body = Build(filter.Value, parameter, fields, 0, ref nodes);
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    static Expression Build(JsonElement node, ParameterExpression parameter, IReadOnlyDictionary<string, EntityField> fields, int depth, ref int nodes)
    {
        if (depth > MaxDepth)
            throw new EntityAIToolException($"Filter is nested more than {MaxDepth} levels deep.");
        if (++nodes > MaxNodes)
            throw new EntityAIToolException($"Filter has more than {MaxNodes} conditions.");
        if (node.ValueKind != JsonValueKind.Object)
            throw new EntityAIToolException("Filter node must be a JSON object.");

        // Combinators are mutually exclusive with each other and with leaf comparisons: a node that mixes them is
        // ambiguous, and guessing which half the model meant is how a filter quietly becomes broader than intended.
        var hasAnd = node.TryGetProperty("and", out var andNode);
        var hasOr = node.TryGetProperty("or", out var orNode);
        var hasNot = node.TryGetProperty("not", out var notNode);
        var hasLeaf = node.TryGetProperty("field", out var fieldNode);
        if ((hasAnd ? 1 : 0) + (hasOr ? 1 : 0) + (hasNot ? 1 : 0) + (hasLeaf ? 1 : 0) != 1)
            throw new EntityAIToolException(
                "Each filter node must be exactly one of: a leaf {field, op, value}, {and:[...]}, {or:[...]} or {not:{...}}.");

        if (hasAnd)
            return Combine(andNode, "and", parameter, fields, depth, ref nodes);
        if (hasOr)
            return Combine(orNode, "or", parameter, fields, depth, ref nodes);
        if (hasNot)
            return Expression.Not(Build(notNode, parameter, fields, depth + 1, ref nodes));

        if (!node.TryGetProperty("op", out var opNode) || opNode.ValueKind != JsonValueKind.String)
            throw new EntityAIToolException("Leaf filter requires a string 'op' property.");
        if (fieldNode.ValueKind != JsonValueKind.String)
            throw new EntityAIToolException("'field' must be a string.");

        var field = Resolve(fields, fieldNode.GetString()!);
        node.TryGetProperty("value", out var valueNode);
        return BuildLeaf(parameter, field, opNode.GetString()!, valueNode);
    }

    /// <summary>Looks a model-named field up in the closed set. Unknown and hidden fields get the same answer.</summary>
    public static EntityField Resolve(IReadOnlyDictionary<string, EntityField> fields, string name)
        => fields.TryGetValue(name, out var field)
            ? field
            : throw new EntityAIToolException($"Field '{Truncate(name)}' is unknown or not exposed by this tool.");

    static Expression Combine(JsonElement array, string kind, ParameterExpression parameter, IReadOnlyDictionary<string, EntityField> fields, int depth, ref int nodes)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
            throw new EntityAIToolException($"'{kind}' must be a non-empty array.");

        Expression? acc = null;
        foreach (var item in array.EnumerateArray())
        {
            var expr = Build(item, parameter, fields, depth + 1, ref nodes);
            acc = acc is null ? expr : kind == "and" ? Expression.AndAlso(acc, expr) : Expression.OrElse(acc, expr);
        }
        return acc!;
    }

    static Expression BuildLeaf(ParameterExpression parameter, EntityField field, string op, JsonElement value)
    {
        var member = field.Access(parameter);

        switch (op)
        {
            case "in":
                if (value.ValueKind != JsonValueKind.Array)
                    throw new EntityAIToolException("'in' requires an array value.");
                if (value.GetArrayLength() > MaxInValues)
                    throw new EntityAIToolException($"'in' accepts at most {MaxInValues} values.");

                Expression? acc = null;
                foreach (var item in value.EnumerateArray())
                {
                    var eq = Expression.Equal(member, Parameter(ScalarTypes.FromJson(item, field.ClrType, field.Name), field.ClrType));
                    acc = acc is null ? eq : Expression.OrElse(acc, eq);
                }
                return acc ?? Expression.Constant(false);

            case "contains":
            case "startsWith":
            case "endsWith":
                if (field.ClrType != typeof(string))
                    throw new EntityAIToolException($"Operator '{op}' requires a string field; '{field.Name}' is not one.");
                if (value.ValueKind != JsonValueKind.String)
                    throw new EntityAIToolException($"'{op}' requires a string value.");

                var method = op switch
                {
                    "contains" => StringContains,
                    "startsWith" => StringStartsWith,
                    _ => StringEndsWith
                };
                // x.Name != null && x.Name.Contains(@p): the explicit guard keeps the tree null-safe C#, which is what EF translates.
                return Expression.AndAlso(
                    Expression.NotEqual(member, Expression.Constant(null, typeof(string))),
                    Expression.Call(member, method, Parameter(value.GetString(), typeof(string))));

            case "eq":
            case "ne":
            case "gt":
            case "gte":
            case "lt":
            case "lte":
                var operand = ScalarTypes.FromJson(value, field.ClrType, field.Name);
                if (operand is null && op is not ("eq" or "ne"))
                    throw new EntityAIToolException($"Operator '{op}' cannot compare with null.");

                // A null value is a constant (IS NULL / IS NOT NULL); everything else is a parameter.
                var right = operand is null ? Expression.Constant(null, field.ClrType) : Parameter(operand, field.ClrType);
                if (op is not ("eq" or "ne") && !ScalarTypes.IsOrderable(field.ClrType) && field.ClrType != typeof(string))
                    throw new EntityAIToolException(
                        $"Operator '{op}' is not supported on field '{field.Name}'; use 'eq', 'ne' or 'in'.");

                return op switch
                {
                    "eq" => Expression.Equal(member, right),
                    "ne" => Expression.NotEqual(member, right),
                    _ => Compare(member, right, op)
                };

            default:
                throw new EntityAIToolException($"Unknown operator '{Truncate(op)}'.");
        }
    }

    // Strings have no C# relational operators, so gt/lt on a string goes through string.Compare — which EF translates.
    static readonly MethodInfo StringCompare = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;

    static Expression Compare(Expression member, Expression right, string op)
    {
        Expression left = member;
        if (member.Type == typeof(string))
        {
            left = Expression.Call(StringCompare, member, right);
            right = Expression.Constant(0);
        }

        return op switch
        {
            "gt" => Expression.GreaterThan(left, right),
            "gte" => Expression.GreaterThanOrEqual(left, right),
            "lt" => Expression.LessThan(left, right),
            _ => Expression.LessThanOrEqual(left, right)
        };
    }

    /// <summary>
    /// A typed, captured value — the same shape C# emits for a closure variable — so EF sends it as a SQL parameter
    /// and reuses one cached query plan per filter shape instead of compiling one per value.
    /// </summary>
    public static Expression Parameter(object? value, Type type)
    {
        var boxType = typeof(StrongBox<>).MakeGenericType(type);
        var box = Activator.CreateInstance(boxType, value)!;
        return Expression.Field(Expression.Constant(box), boxType.GetField(nameof(StrongBox<object>.Value))!);
    }

    static string Truncate(string raw) => raw.Length <= 64 ? raw : raw[..64] + "…";
}
