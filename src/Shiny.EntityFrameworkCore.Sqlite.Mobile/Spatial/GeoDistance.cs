using NetTopologySuite.Geometries;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Spatial;

/// <summary>
/// Great-circle (haversine) distance in meters between NetTopologySuite geometries holding WGS84 longitude (X) /
/// latitude (Y). Point-to-geometry is exact to the nearest edge (0 when the point is inside a polygon);
/// geometry-to-geometry is a vertex-sampled approximation adequate for filtering and ordering.
/// Ported from Shiny.DocumentDb's <c>GeoDistance</c>.
/// </summary>
static class GeoDistance
{
    public const double EarthRadiusMeters = 6_371_000.0;

    public static double Between(Geometry a, Geometry b)
    {
        if (a.IsEmpty || b.IsEmpty)
            return double.PositiveInfinity;

        if (a is Point pa)
            return PointToGeometry(pa.Coordinate, b);

        if (b is Point pb)
            return PointToGeometry(pb.Coordinate, a);

        if (a.Intersects(b))
            return 0;

        var min = double.PositiveInfinity;
        foreach (var c in a.Coordinates)
            min = Math.Min(min, PointToGeometry(c, b));
        foreach (var c in b.Coordinates)
            min = Math.Min(min, PointToGeometry(c, a));
        return min;
    }

    public static double Haversine(Coordinate a, Coordinate b)
    {
        var dLat = ToRadians(b.Y - a.Y);
        var dLon = ToRadians(b.X - a.X);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(a.Y)) * Math.Cos(ToRadians(b.Y)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
    }

    static double PointToGeometry(Coordinate p, Geometry g)
    {
        switch (g)
        {
            case Point pt:
                return pt.IsEmpty ? double.PositiveInfinity : Haversine(p, pt.Coordinate);

            case LineString ls:
                return MinToRing(p, ls.Coordinates);

            case Polygon pg:
                if (pg.IsEmpty)
                    return double.PositiveInfinity;
                if (pg.Covers(pg.Factory.CreatePoint(p)))
                    return 0;
                var d = MinToRing(p, pg.ExteriorRing.Coordinates);
                foreach (var hole in pg.InteriorRings)
                    d = Math.Min(d, MinToRing(p, hole.Coordinates));
                return d;

            default:
                // Multi* and GeometryCollection: nearest member.
                var min = double.PositiveInfinity;
                for (var i = 0; i < g.NumGeometries; i++)
                {
                    var member = g.GetGeometryN(i);
                    if (!ReferenceEquals(member, g))
                        min = Math.Min(min, PointToGeometry(p, member));
                }
                return min;
        }
    }

    static double MinToRing(Coordinate p, Coordinate[] ring)
    {
        if (ring.Length == 0)
            return double.PositiveInfinity;

        if (ring.Length == 1)
            return Haversine(p, ring[0]);

        var min = double.PositiveInfinity;
        for (var i = 0; i < ring.Length - 1; i++)
            min = Math.Min(min, Haversine(p, NearestOnSegment(p, ring[i], ring[i + 1])));
        return min;
    }

    // Planar projection in lon/lat space, then haversine to the projected point. The projected point always lies on
    // the segment, so the result is a real distance to the geometry (never smaller than the true minimum).
    static Coordinate NearestOnSegment(Coordinate p, Coordinate a, Coordinate b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var lenSq = dx * dx + dy * dy;
        if (lenSq == 0)
            return a;

        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
        t = Math.Max(0, Math.Min(1, t));
        return new Coordinate(a.X + t * dx, a.Y + t * dy);
    }

    static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
