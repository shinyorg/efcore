using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using NetTopologySuite.Geometries;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Spatial;

sealed class GeometryTypeMappingSourcePlugin : IRelationalTypeMappingSourcePlugin
{
    public RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType;
        var storeType = mappingInfo.StoreTypeName;
        return clrType != null
               && typeof(Geometry).IsAssignableFrom(clrType)
               && (storeType == null || string.Equals(storeType, GeometryTypeMapping.StoreTypeName, StringComparison.OrdinalIgnoreCase))
            ? GeometryTypeMapping.For(clrType)
            : null;
    }
}

sealed class SpatialMethodCallTranslatorPlugin(ISqlExpressionFactory sqlExpressionFactory) : IMethodCallTranslatorPlugin
{
    public IEnumerable<IMethodCallTranslator> Translators { get; } = [new SpatialMethodCallTranslator(sqlExpressionFactory)];
}

sealed class SpatialMemberTranslatorPlugin(ISqlExpressionFactory sqlExpressionFactory) : IMemberTranslatorPlugin
{
    public IEnumerable<IMemberTranslator> Translators { get; } = [new SpatialMemberTranslator(sqlExpressionFactory)];
}

/// <summary>
/// Translates NetTopologySuite's instance predicates (planar, exactly as NTS evaluates them in memory) and the
/// geodesic <c>EF.Functions.GeoDistanceMeters</c> / <c>IsWithinMeters</c> helpers into calls to the
/// <c>shiny_st_*</c> / <c>shiny_geo_*</c> UDFs.
/// </summary>
sealed class SpatialMethodCallTranslator(ISqlExpressionFactory sql) : IMethodCallTranslator
{
    static readonly Dictionary<MethodInfo, string> predicates = new()
    {
        [GeometryMethod(nameof(Geometry.Intersects))] = SqliteSpatial.Intersects,
        [GeometryMethod(nameof(Geometry.Contains))] = SqliteSpatial.Contains,
        [GeometryMethod(nameof(Geometry.Within))] = SqliteSpatial.Within,
        [GeometryMethod(nameof(Geometry.Covers))] = SqliteSpatial.Covers,
        [GeometryMethod(nameof(Geometry.CoveredBy))] = SqliteSpatial.CoveredBy,
        [GeometryMethod(nameof(Geometry.Touches))] = SqliteSpatial.Touches,
        [GeometryMethod(nameof(Geometry.Crosses))] = SqliteSpatial.Crosses,
        [GeometryMethod(nameof(Geometry.Overlaps))] = SqliteSpatial.Overlaps,
        [GeometryMethod(nameof(Geometry.Disjoint))] = SqliteSpatial.Disjoint,
        [GeometryMethod(nameof(Geometry.EqualsTopologically))] = SqliteSpatial.EqualsTopologically
    };

    internal static readonly MethodInfo DistanceMethod = GeometryMethod(nameof(Geometry.Distance));
    internal static readonly MethodInfo IsWithinDistanceMethod = typeof(Geometry).GetMethod(nameof(Geometry.IsWithinDistance), [typeof(Geometry), typeof(double)])!;

    internal static readonly MethodInfo GeoDistanceMetersMethod = typeof(SqliteMobileDbFunctionsExtensions).GetMethod(nameof(SqliteMobileDbFunctionsExtensions.GeoDistanceMeters))!;
    internal static readonly MethodInfo IsWithinMetersMethod = typeof(SqliteMobileDbFunctionsExtensions).GetMethod(nameof(SqliteMobileDbFunctionsExtensions.IsWithinMeters))!;

    /// <summary>The NTS predicates whose truth implies the two bounding boxes intersect (everything except Disjoint).</summary>
    internal static bool ImpliesEnvelopeOverlap(MethodInfo method)
        => method.DeclaringType != null
           && typeof(Geometry).IsAssignableFrom(method.DeclaringType)
           && predicates.TryGetValue(method.GetBaseDefinition(), out var name)
           && name != SqliteSpatial.Disjoint;

    public SqlExpression? Translate(SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments, IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (instance != null && method.DeclaringType != null && typeof(Geometry).IsAssignableFrom(method.DeclaringType))
        {
            var baseMethod = method.GetBaseDefinition();
            if (predicates.TryGetValue(baseMethod, out var name))
                return this.Function(name, [instance, arguments[0]], typeof(bool));

            if (baseMethod == DistanceMethod)
                return this.Function(SqliteSpatial.Distance, [instance, arguments[0]], typeof(double));

            if (baseMethod == IsWithinDistanceMethod)
                return this.Function(SqliteSpatial.IsWithinDistance, [instance, arguments[0], arguments[1]], typeof(bool));
        }

        if (method == GeoDistanceMetersMethod)
            return this.Function(SqliteSpatial.GeoDistanceMeters, [arguments[1], arguments[2]], typeof(double));

        if (method == IsWithinMetersMethod)
            return this.Function(SqliteSpatial.IsWithinMeters, [arguments[1], arguments[2], arguments[3]], typeof(bool));

        return null;
    }

    SqlExpression Function(string name, SqlExpression[] args, Type returnType)
    {
        // Each geometry operand is mapped by its own CLR type rather than inferring one mapping from the other side:
        // the converter casts to its geometry type, so a Point column's mapping cannot write a captured Polygon.
        var typed = new SqlExpression[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            typed[i] = args[i].TypeMapping != null
                ? args[i]
                : i < 2
                    ? sql.ApplyTypeMapping(args[i], GeometryTypeMapping.For(args[i].Type))
                    : sql.ApplyDefaultTypeMapping(args[i]);
        }

        return sql.Function(
            name,
            typed,
            nullable: true,
            argumentsPropagateNullability: Enumerable.Repeat(true, typed.Length),
            returnType);
    }

    static MethodInfo GeometryMethod(string name) => typeof(Geometry).GetMethod(name, [typeof(Geometry)])!;
}

/// <summary>Translates a few common NTS properties (<c>Point.X/Y</c>, <c>Area</c>, <c>Length</c>, <c>SRID</c>, <c>IsEmpty</c>).</summary>
sealed class SpatialMemberTranslator(ISqlExpressionFactory sql) : IMemberTranslator
{
    public SqlExpression? Translate(SqlExpression? instance, MemberInfo member, Type returnType, IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (instance == null || member.DeclaringType == null || !typeof(Geometry).IsAssignableFrom(member.DeclaringType))
            return null;

        var name = member.Name switch
        {
            nameof(Point.X) when typeof(Point).IsAssignableFrom(member.DeclaringType) => SqliteSpatial.X,
            nameof(Point.Y) when typeof(Point).IsAssignableFrom(member.DeclaringType) => SqliteSpatial.Y,
            nameof(Geometry.Area) => SqliteSpatial.Area,
            nameof(Geometry.Length) => SqliteSpatial.Length,
            nameof(Geometry.SRID) => SqliteSpatial.Srid,
            nameof(Geometry.IsEmpty) => SqliteSpatial.IsEmpty,
            _ => null
        };

        return name == null
            ? null
            : sql.Function(name, [instance], nullable: true, argumentsPropagateNullability: [true], returnType);
    }
}
