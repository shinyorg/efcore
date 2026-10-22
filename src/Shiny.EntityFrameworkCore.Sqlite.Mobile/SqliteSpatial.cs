using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Spatial;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile;

/// <summary>
/// The SQLite scalar functions that evaluate NetTopologySuite predicates in SQL. <c>UseShinySpatial()</c> registers
/// them on every connection EF uses, so you only need this for connections EF never touches - a raw
/// <see cref="SqliteConnection"/> that writes to a table with a spatial index (its R*Tree triggers call
/// <c>shiny_st_bbox</c>) or runs hand-written spatial SQL.
/// </summary>
public static class SqliteSpatial
{
    internal const string Intersects = "shiny_st_intersects";
    internal const string Contains = "shiny_st_contains";
    internal const string Within = "shiny_st_within";
    internal const string Covers = "shiny_st_covers";
    internal const string CoveredBy = "shiny_st_coveredby";
    internal const string Touches = "shiny_st_touches";
    internal const string Crosses = "shiny_st_crosses";
    internal const string Overlaps = "shiny_st_overlaps";
    internal const string Disjoint = "shiny_st_disjoint";
    internal const string EqualsTopologically = "shiny_st_equals";
    internal const string IsWithinDistance = "shiny_st_dwithin";
    internal const string Distance = "shiny_st_distance";
    internal const string GeoDistanceMeters = "shiny_geo_distance";
    internal const string IsWithinMeters = "shiny_geo_dwithin";
    internal const string Bound = "shiny_st_bbox";
    internal const string X = "shiny_st_x";
    internal const string Y = "shiny_st_y";
    internal const string Area = "shiny_st_area";
    internal const string Length = "shiny_st_length";
    internal const string Srid = "shiny_st_srid";
    internal const string IsEmpty = "shiny_st_isempty";

    /// <summary>
    /// Registers the <c>shiny_st_*</c> / <c>shiny_geo_*</c> functions on <paramref name="connection"/>. Safe to call
    /// before or after the connection is opened (Microsoft.Data.Sqlite re-applies them on every open) and
    /// idempotent.
    /// </summary>
    public static void RegisterFunctions(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Predicate(connection, Intersects, static (a, b) => a.Intersects(b));
        Predicate(connection, Contains, static (a, b) => a.Contains(b));
        Predicate(connection, Within, static (a, b) => a.Within(b));
        Predicate(connection, Covers, static (a, b) => a.Covers(b));
        Predicate(connection, CoveredBy, static (a, b) => a.CoveredBy(b));
        Predicate(connection, Touches, static (a, b) => a.Touches(b));
        Predicate(connection, Crosses, static (a, b) => a.Crosses(b));
        Predicate(connection, Overlaps, static (a, b) => a.Overlaps(b));
        Predicate(connection, Disjoint, static (a, b) => a.Disjoint(b));
        Predicate(connection, EqualsTopologically, static (a, b) => a.EqualsTopologically(b));

        connection.CreateFunction<byte[]?, byte[]?, double?, bool?>(
            IsWithinDistance,
            static (a, b, d) => a == null || b == null || d == null ? null : Read(a).IsWithinDistance(Read(b), d.Value),
            isDeterministic: true);

        connection.CreateFunction<byte[]?, byte[]?, double?>(
            Distance,
            static (a, b) => a == null || b == null ? null : Read(a).Distance(Read(b)),
            isDeterministic: true);

        connection.CreateFunction<byte[]?, byte[]?, double?>(
            GeoDistanceMeters,
            static (a, b) => a == null || b == null ? null : GeoDistance.Between(Read(a), Read(b)),
            isDeterministic: true);

        connection.CreateFunction<byte[]?, byte[]?, double?, bool?>(
            IsWithinMeters,
            static (a, b, m) => a == null || b == null || m == null ? null : GeoDistance.Between(Read(a), Read(b)) <= m.Value,
            isDeterministic: true);

        connection.CreateFunction<byte[]?, long, double, double, double?>(
            Bound,
            static (g, part, expand, meters) => g == null ? null : SpatialBounds.Bound(Read(g), part, expand, meters),
            isDeterministic: true);

        Scalar(connection, X, static g => g is Point p && !p.IsEmpty ? p.X : null);
        Scalar(connection, Y, static g => g is Point p && !p.IsEmpty ? p.Y : null);
        Scalar(connection, Area, static g => g.Area);
        Scalar(connection, Length, static g => g.Length);

        connection.CreateFunction<byte[]?, long?>(
            Srid,
            static g => g == null ? null : Read(g).SRID,
            isDeterministic: true);

        connection.CreateFunction<byte[]?, bool?>(
            IsEmpty,
            static g => g == null ? null : Read(g).IsEmpty,
            isDeterministic: true);
    }

    static void Predicate(SqliteConnection connection, string name, Func<Geometry, Geometry, bool> predicate)
        => connection.CreateFunction<byte[]?, byte[]?, bool?>(
            name,
            (a, b) => a == null || b == null ? null : predicate(Read(a), Read(b)),
            isDeterministic: true);

    static void Scalar(SqliteConnection connection, string name, Func<Geometry, double?> selector)
        => connection.CreateFunction<byte[]?, double?>(
            name,
            g => g == null ? null : selector(Read(g)),
            isDeterministic: true);

    static Geometry Read(byte[] wkb) => GeometryWkb.Read(wkb);
}
