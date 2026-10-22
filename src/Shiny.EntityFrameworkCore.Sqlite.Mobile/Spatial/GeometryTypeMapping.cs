using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NetTopologySuite.Geometries;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Spatial;

/// <summary>
/// Maps any NetTopologySuite <see cref="Geometry"/> (or subclass) to a SQLite <c>BLOB</c> holding EWKB.
/// The store type is deliberately <c>BLOB</c>, not <c>GEOMETRY</c>/<c>POINT</c>: EF's SQLite migrations generator
/// treats those names as SpatiaLite columns and emits <c>AddGeometryColumn()</c>, which needs <c>mod_spatialite</c>.
/// </summary>
sealed class GeometryTypeMapping : RelationalTypeMapping
{
    public const string StoreTypeName = "BLOB";

    static readonly ConcurrentDictionary<Type, GeometryTypeMapping> cache = new();

    GeometryTypeMapping(Type clrType)
        : base(new RelationalTypeMappingParameters(
            new CoreTypeMappingParameters(
                clrType,
                (ValueConverter)Activator.CreateInstance(typeof(GeometryWkbConverter<>).MakeGenericType(clrType))!,
                (ValueComparer)Activator.CreateInstance(typeof(GeometryValueComparer<>).MakeGenericType(clrType))!),
            StoreTypeName,
            StoreTypePostfix.None,
            System.Data.DbType.Binary))
    {
    }

    GeometryTypeMapping(RelationalTypeMappingParameters parameters) : base(parameters)
    {
    }

    public static GeometryTypeMapping For(Type clrType) => cache.GetOrAdd(clrType, static t => new GeometryTypeMapping(t));

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) => new GeometryTypeMapping(parameters);

    // Receives the provider (WKB) value: RelationalTypeMapping runs the converter before asking for a literal.
    protected override string GenerateNonNullSqlLiteral(object value) => "X'" + Convert.ToHexString((byte[])value) + "'";
}

/// <summary>Geometry ⇄ EWKB converter for one concrete geometry CLR type.</summary>
sealed class GeometryWkbConverter<TGeometry> : ValueConverter<TGeometry, byte[]> where TGeometry : Geometry
{
    public GeometryWkbConverter()
        : base(g => GeometryWkb.Write(g), b => (TGeometry)GeometryWkb.Read(b))
    {
    }
}

/// <summary>
/// Geometries are mutable reference types, so change tracking snapshots a copy and compares exactly (including
/// SRID) - otherwise an in-place coordinate edit would never be detected as a change.
/// </summary>
sealed class GeometryValueComparer<TGeometry> : ValueComparer<TGeometry> where TGeometry : Geometry
{
    public GeometryValueComparer()
        : base(
            (a, b) => a == null ? b == null : b != null && a.SRID == b.SRID && a.EqualsExact(b),
            g => g.GetHashCode(),
            g => (TGeometry)g.Copy())
    {
    }
}
