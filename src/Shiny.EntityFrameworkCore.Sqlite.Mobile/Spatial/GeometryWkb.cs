using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Spatial;

/// <summary>
/// The on-disk geometry format: little-endian extended WKB (EWKB) with the SRID embedded, so a round trip keeps
/// <see cref="Geometry.SRID"/>. Z and M ordinates are written only when the geometry carries them.
/// </summary>
static class GeometryWkb
{
    // WKBReader/WKBWriter keep per-call state, so each thread gets its own instances.
    [ThreadStatic] static WKBReader? reader;

    public static byte[] Write(Geometry geometry)
    {
        var first = geometry.Coordinate;
        var hasZ = first != null && !double.IsNaN(first.Z);
        var hasM = first != null && !double.IsNaN(first.M);
        return new WKBWriter(ByteOrder.LittleEndian, true, hasZ, hasM).Write(geometry);
    }

    public static Geometry Read(byte[] wkb) => (reader ??= new WKBReader()).Read(wkb);
}
