using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Vectors;

/// <summary>
/// Maps <c>float[]</c> / <c>ReadOnlyMemory&lt;float&gt;</c> to a <c>BLOB</c> in sqlite-vec's float32 format (the raw
/// little-endian floats), which is what <c>vec0</c> and the <c>vec_distance_*</c> functions read natively.
/// </summary>
sealed class VectorTypeMapping : RelationalTypeMapping
{
    public const string StoreTypeName = "BLOB";

    public static readonly VectorTypeMapping Array = new(typeof(float[]), new FloatArrayConverter(), new FloatArrayComparer());
    public static readonly VectorTypeMapping Memory = new(typeof(ReadOnlyMemory<float>), new FloatMemoryConverter(), new FloatMemoryComparer());

    VectorTypeMapping(Type clrType, ValueConverter converter, ValueComparer comparer)
        : base(new RelationalTypeMappingParameters(
            new CoreTypeMappingParameters(clrType, converter, comparer),
            StoreTypeName,
            StoreTypePostfix.None,
            System.Data.DbType.Binary))
    {
    }

    VectorTypeMapping(RelationalTypeMappingParameters parameters) : base(parameters)
    {
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) => new VectorTypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value) => "X'" + Convert.ToHexString((byte[])value) + "'";

    internal static byte[] ToBlob(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    internal static byte[] ToBlob(ReadOnlyMemory<float> vector) => MemoryMarshal.AsBytes(vector.Span).ToArray();

    internal static float[] FromBlob(byte[] blob) => MemoryMarshal.Cast<byte, float>(blob).ToArray();

    internal static bool SequenceEqual(float[]? a, float[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);

    internal static bool SequenceEqual(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b) => a.Span.SequenceEqual(b.Span);

    internal static int Hash(float[] vector) => Hash(new ReadOnlyMemory<float>(vector));

    internal static int Hash(ReadOnlyMemory<float> vector)
    {
        var hash = new HashCode();
        hash.AddBytes(MemoryMarshal.AsBytes(vector.Span));
        return hash.ToHashCode();
    }

    internal static ReadOnlyMemory<float> Copy(ReadOnlyMemory<float> vector) => new(vector.ToArray());
}

sealed class FloatArrayConverter() : ValueConverter<float[], byte[]>(
    v => VectorTypeMapping.ToBlob(v),
    b => VectorTypeMapping.FromBlob(b));

sealed class FloatMemoryConverter() : ValueConverter<ReadOnlyMemory<float>, byte[]>(
    v => VectorTypeMapping.ToBlob(v),
    b => new ReadOnlyMemory<float>(VectorTypeMapping.FromBlob(b)));

sealed class FloatArrayComparer() : ValueComparer<float[]>(
    (a, b) => VectorTypeMapping.SequenceEqual(a, b),
    v => VectorTypeMapping.Hash(v),
    v => v.ToArray());

sealed class FloatMemoryComparer() : ValueComparer<ReadOnlyMemory<float>>(
    (a, b) => VectorTypeMapping.SequenceEqual(a, b),
    v => VectorTypeMapping.Hash(v),
    v => VectorTypeMapping.Copy(v));
