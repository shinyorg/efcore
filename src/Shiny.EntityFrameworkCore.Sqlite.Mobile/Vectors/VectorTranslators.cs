using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Vectors;

/// <summary>
/// Maps vectors to sqlite-vec's float32 <c>BLOB</c>. <c>float[]</c> is claimed only when the column type is <c>BLOB</c>
/// (which <see cref="VectorColumnConvention"/> sets on every <c>float[]</c> property by convention): a bare
/// <c>float[]</c> lookup is also how EF types a captured array in <c>array.Contains(x.Value)</c>, and that must keep
/// EF's own collection mapping. <c>ReadOnlyMemory&lt;float&gt;</c> has no other meaning, so it is always a vector.
/// </summary>
sealed class VectorTypeMappingSourcePlugin : IRelationalTypeMappingSourcePlugin
{
    public RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        var storeType = mappingInfo.StoreTypeName;
        var isBlob = string.Equals(storeType, VectorTypeMapping.StoreTypeName, StringComparison.OrdinalIgnoreCase);
        var clrType = mappingInfo.ClrType;

        if (clrType == typeof(float[]))
            return isBlob ? VectorTypeMapping.Array : null;

        return clrType == typeof(ReadOnlyMemory<float>) && (storeType == null || isBlob) ? VectorTypeMapping.Memory : null;
    }
}

/// <summary>
/// Makes every <c>float[]</c> property a vector column (<c>BLOB</c>) by convention once vectors are enabled. Because
/// it is only a convention, <c>HasColumnType("TEXT")</c> on a property keeps EF's JSON primitive-collection mapping.
/// </summary>
sealed class VectorColumnConvention : IPropertyAddedConvention
{
    public void ProcessPropertyAdded(IConventionPropertyBuilder propertyBuilder, IConventionContext<IConventionPropertyBuilder> context)
    {
        var type = propertyBuilder.Metadata.ClrType;
        if (type == typeof(float[]) || type == typeof(ReadOnlyMemory<float>) || type == typeof(ReadOnlyMemory<float>?))
            propertyBuilder.HasColumnType(VectorTypeMapping.StoreTypeName);
    }
}

sealed class VectorMethodCallTranslatorPlugin(ISqlExpressionFactory sqlExpressionFactory) : IMethodCallTranslatorPlugin
{
    public IEnumerable<IMethodCallTranslator> Translators { get; } = [new VectorMethodCallTranslator(sqlExpressionFactory)];
}

/// <summary>
/// <c>EF.Functions.VectorDistance(a, b, metric)</c> → <c>vec_distance_cosine|l2|l1(a, b)</c>: an exact brute-force
/// distance usable in <c>Where</c>/<c>OrderBy</c> without any index.
/// </summary>
sealed class VectorMethodCallTranslator(ISqlExpressionFactory sql) : IMethodCallTranslator
{
    static readonly MethodInfo[] distanceMethods = typeof(SqliteMobileDbFunctionsExtensions)
        .GetMethods()
        .Where(m => m.Name == nameof(SqliteMobileDbFunctionsExtensions.VectorDistance))
        .ToArray();

    public SqlExpression? Translate(SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments, IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (!distanceMethods.Contains(method))
            return null;

        var a = Typed(arguments[1]);
        var b = Typed(arguments[2]);
        var metric = arguments[3];

        if (metric is SqlConstantExpression { Value: VectorDistanceMetric constant })
            return this.Distance(constant, a, b);

        // A captured (parameterised) metric: pick the function at execution time.
        var mappedMetric = sql.ApplyDefaultTypeMapping(metric);
        return sql.Case(
            Enum.GetValues<VectorDistanceMetric>()
                .Select(m => new CaseWhenClause(
                    sql.Equal(mappedMetric, sql.Constant(m, mappedMetric.TypeMapping)),
                    this.Distance(m, a, b)))
                .ToList(),
            elseResult: null);
    }

    SqlExpression Typed(SqlExpression argument)
        => argument.TypeMapping != null
            ? argument
            : sql.ApplyTypeMapping(argument, argument.Type == typeof(ReadOnlyMemory<float>) ? VectorTypeMapping.Memory : VectorTypeMapping.Array);

    SqlExpression Distance(VectorDistanceMetric metric, SqlExpression a, SqlExpression b)
        => sql.Function(
            "vec_distance_" + SidecarSql.MetricKeyword(metric),
            [a, b],
            nullable: true,
            argumentsPropagateNullability: [true, true],
            typeof(double));
}
