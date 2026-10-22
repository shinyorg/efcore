using NetTopologySuite.Geometries;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Spatial;

/// <summary>
/// Bounding-box math for the R*Tree. <c>part</c> is 0 = min X, 1 = max X, 2 = min Y, 3 = max Y. The box can be
/// grown by a planar distance (NTS <c>IsWithinDistance</c>) or by a geodesic distance in meters
/// (<c>IsWithinMeters</c>); the geodesic growth is conservative so the R*Tree never drops a true match.
/// </summary>
static class SpatialBounds
{
    // Wider than any float32 coordinate the R*Tree can store - "no bound on this axis".
    const double Unbounded = 3.0e38;

    public static double? Bound(Geometry? geometry, long part, double planarExpand, double meters)
    {
        if (geometry == null || geometry.IsEmpty)
            return null;

        var env = geometry.EnvelopeInternal;
        double minX = env.MinX, maxX = env.MaxX, minY = env.MinY, maxY = env.MaxY;

        if (planarExpand > 0)
        {
            minX -= planarExpand;
            maxX += planarExpand;
            minY -= planarExpand;
            maxY += planarExpand;
        }

        if (meters > 0)
        {
            // Along a meridian, meters → degrees of latitude is exact for the haversine sphere.
            var angular = meters / GeoDistance.EarthRadiusMeters; // radians
            var dLat = angular * 180.0 / Math.PI;

            // Longitude reach of a circle of angular radius δ centred at latitude φ is asin(sin δ / cos φ); the
            // worst case is the envelope edge furthest from the equator.
            var phi = Math.Max(Math.Abs(minY), Math.Abs(maxY)) * Math.PI / 180.0;
            var ratio = Math.Sin(Math.Min(angular, Math.PI / 2)) / Math.Cos(phi);
            var dLon = ratio >= 1 || angular >= Math.PI / 2 ? double.PositiveInfinity : Math.Asin(ratio) * 180.0 / Math.PI;

            // Small safety margin for float rounding.
            dLat *= 1.0001;
            dLon *= 1.0001;

            minY -= dLat;
            maxY += dLat;
            if (double.IsInfinity(dLon) || minX - dLon < -180 || maxX + dLon > 180)
            {
                // Reaches a pole or crosses the antimeridian: a lon/lat box can't express it, so don't constrain X.
                minX = -Unbounded;
                maxX = Unbounded;
            }
            else
            {
                minX -= dLon;
                maxX += dLon;
            }
        }

        return part switch
        {
            0 => minX,
            1 => maxX,
            2 => minY,
            3 => maxY,
            _ => throw new ArgumentOutOfRangeException(nameof(part))
        };
    }
}
