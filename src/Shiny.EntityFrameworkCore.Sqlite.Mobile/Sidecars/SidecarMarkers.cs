using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using NetTopologySuite.Geometries;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Spatial;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Vectors;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

/// <summary>
/// Marker methods that only ever appear in expression trees this package builds (query rewriting and
/// <c>NearestTo</c>); <see cref="SidecarMethodCallTranslator"/> turns them into SQL. They are never executed.
/// </summary>
static class SidecarMarkers
{
    public static readonly MethodInfo BoundMethod = typeof(SidecarMarkers).GetMethod(nameof(Bound))!;
    public static readonly MethodInfo TextMatchMethod = typeof(SidecarMarkers).GetMethod(nameof(TextMatch))!;
    public static readonly MethodInfo VectorMatchArrayMethod = typeof(SidecarMarkers).GetMethod(nameof(VectorMatch), [typeof(byte[]), typeof(float[])])!;
    public static readonly MethodInfo VectorMatchMemoryMethod = typeof(SidecarMarkers).GetMethod(nameof(VectorMatch), [typeof(byte[]), typeof(ReadOnlyMemory<float>)])!;

    /// <summary>One edge of <paramref name="geometry"/>'s bounding box, grown by a planar distance or by meters.</summary>
    public static double Bound(Geometry? geometry, int part, double planarExpand, double meters) => throw NotSql();

    /// <summary><c>match(query, column)</c> - the function form of <c>column MATCH query</c> for FTS5.</summary>
    public static bool TextMatch(string? column, string? query) => throw NotSql();

    /// <summary><c>match(query, column)</c> for a vec0 KNN query.</summary>
    public static bool VectorMatch(byte[]? column, float[]? query) => throw NotSql();

    /// <summary><c>match(query, column)</c> for a vec0 KNN query.</summary>
    public static bool VectorMatch(byte[]? column, ReadOnlyMemory<float> query) => throw NotSql();

    static InvalidOperationException NotSql() => new("This method is only for use inside EF Core queries.");
}

sealed class SidecarMethodCallTranslatorPlugin(ISqlExpressionFactory sqlExpressionFactory) : IMethodCallTranslatorPlugin
{
    public IEnumerable<IMethodCallTranslator> Translators { get; } = [new SidecarMethodCallTranslator(sqlExpressionFactory)];
}

sealed class SidecarMethodCallTranslator(ISqlExpressionFactory sql) : IMethodCallTranslator
{
    public SqlExpression? Translate(SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments, IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method == SidecarMarkers.BoundMethod)
        {
            var geometry = arguments[0].TypeMapping != null ? arguments[0] : sql.ApplyTypeMapping(arguments[0], GeometryTypeMapping.For(arguments[0].Type));
            return sql.Function(
                SqliteSpatial.Bound,
                [geometry, sql.ApplyDefaultTypeMapping(arguments[1]), sql.ApplyDefaultTypeMapping(arguments[2]), sql.ApplyDefaultTypeMapping(arguments[3])],
                nullable: true,
                argumentsPropagateNullability: [true, false, false, false],
                typeof(double));
        }

        if (method == SidecarMarkers.TextMatchMethod)
            return this.Match(arguments[0], sql.ApplyDefaultTypeMapping(arguments[1]));

        if (method == SidecarMarkers.VectorMatchArrayMethod)
            return this.Match(arguments[0], arguments[1].TypeMapping != null ? arguments[1] : sql.ApplyTypeMapping(arguments[1], VectorTypeMapping.Array));

        if (method == SidecarMarkers.VectorMatchMemoryMethod)
            return this.Match(arguments[0], arguments[1].TypeMapping != null ? arguments[1] : sql.ApplyTypeMapping(arguments[1], VectorTypeMapping.Memory));

        return null;
    }

    // SQLite treats match(Y, X) exactly like "X MATCH Y", including handing the constraint to the virtual table's
    // xBestIndex, so the FTS5 / vec0 index is used.
    SqlExpression Match(SqlExpression column, SqlExpression query)
        => sql.Function(
            "match",
            [query, column],
            nullable: true,
            argumentsPropagateNullability: [true, true],
            typeof(bool));
}
