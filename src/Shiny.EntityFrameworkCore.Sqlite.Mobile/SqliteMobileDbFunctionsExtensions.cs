using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Spatial;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile;

/// <summary>
/// <c>EF.Functions</c> extensions for SQLite geodesic distance, vector distance and FTS5 full-text search. The
/// distance functions also run in memory (so they work the same when EF evaluates them client-side); the full-text
/// ones only have meaning inside a query.
/// </summary>
public static class SqliteMobileDbFunctionsExtensions
{
    /// <summary>
    /// Great-circle distance in <b>meters</b> between two WGS84 geometries (X = longitude, Y = latitude). Exact for
    /// point-to-geometry (0 inside a polygon); geometry-to-geometry is vertex-sampled. Use this instead of NTS's
    /// planar <see cref="Geometry.Distance(Geometry)"/>, which returns degrees for lon/lat data. Requires <c>UseShinySpatial()</c>.
    /// </summary>
    public static double GeoDistanceMeters(this DbFunctions _, Geometry? a, Geometry? b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return GeoDistance.Between(a, b);
    }

    /// <summary>
    /// True when the great-circle distance between two WGS84 geometries is at most <paramref name="meters"/>. On a
    /// property with a spatial index the R*Tree pre-filters candidates with a conservatively widened bounding box.
    /// Requires <c>UseShinySpatial()</c>.
    /// </summary>
    public static bool IsWithinMeters(this DbFunctions _, Geometry? a, Geometry? b, double meters)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return GeoDistance.Between(a, b) <= meters;
    }

    /// <summary>
    /// Exact distance between two vectors (sqlite-vec <c>vec_distance_*</c>); smaller is closer. Usable in
    /// <c>Where</c> and <c>OrderBy</c> with no index (brute force). Requires <c>UseShinyVectors()</c>.
    /// </summary>
    public static double VectorDistance(this DbFunctions _, float[]? a, float[]? b, VectorDistanceMetric metric)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Distance(a, b, metric);
    }

    /// <summary>
    /// Exact distance between two vectors (sqlite-vec <c>vec_distance_*</c>); smaller is closer. Requires
    /// <c>UseShinyVectors()</c>.
    /// </summary>
    public static double VectorDistance(this DbFunctions _, ReadOnlyMemory<float> a, ReadOnlyMemory<float> b, VectorDistanceMetric metric)
        => Distance(a.Span, b.Span, metric);

    /// <summary>
    /// True when <paramref name="entity"/>'s full-text index matches <paramref name="query"/> (FTS5 query syntax:
    /// terms, <c>"phrases"</c>, <c>prefix*</c>, <c>AND</c>/<c>OR</c>/<c>NOT</c>, <c>NEAR(...)</c>, <c>column:term</c>).
    /// Use <see cref="FullTextQuery.Escape(string, bool)"/> for raw user input. Requires <c>UseShinyFullText()</c> and
    /// <c>HasFullTextIndex</c> on the entity.
    /// </summary>
    public static bool FullTextMatch<TEntity>(this DbFunctions _, TEntity entity, string query) where TEntity : class
        => throw QueryOnly(nameof(FullTextMatch));

    /// <summary>
    /// True when <paramref name="property"/> (one of the entity's full-text indexed properties) matches
    /// <paramref name="query"/>; the query is scoped to that column with an FTS5 column filter.
    /// </summary>
    public static bool FullTextMatch(this DbFunctions _, string? property, string query)
        => throw QueryOnly(nameof(FullTextMatch));

    /// <summary>
    /// FTS5's <c>rank</c> (bm25) for <paramref name="entity"/> against <paramref name="query"/>: <b>lower is a better
    /// match</b>, so order ascending. Null for rows that don't match - combine with <c>FullTextMatch</c> in the
    /// <c>Where</c>.
    /// </summary>
    public static double? FullTextRank<TEntity>(this DbFunctions _, TEntity entity, string query) where TEntity : class
        => throw QueryOnly(nameof(FullTextRank));

    static InvalidOperationException QueryOnly(string name)
        => new($"EF.Functions.{name} can only be used in an EF Core query against a context configured with UseShinyFullText().");

    static double Distance(ReadOnlySpan<float> a, ReadOnlySpan<float> b, VectorDistanceMetric metric)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Vector dimensions differ ({a.Length} vs {b.Length}).");

        double sum = 0, dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = (double)a[i] - b[i];
            sum += metric == VectorDistanceMetric.Manhattan ? Math.Abs(d) : d * d;
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return metric switch
        {
            VectorDistanceMetric.Cosine => 1 - dot / (Math.Sqrt(na) * Math.Sqrt(nb)),
            VectorDistanceMetric.Euclidean => Math.Sqrt(sum),
            VectorDistanceMetric.Manhattan => sum,
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
    }
}
