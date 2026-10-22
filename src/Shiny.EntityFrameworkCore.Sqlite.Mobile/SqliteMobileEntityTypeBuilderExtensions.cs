using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NetTopologySuite.Geometries;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile;

/// <summary>
/// Declares sidecar indexes. Each one becomes a SQLite virtual table next to the entity table (R*Tree, sqlite-vec
/// <c>vec0</c> or FTS5), created by <c>EnsureCreated</c> or by migrations and kept in sync by SQLite triggers. All three
/// need the entity to have a single integer primary key (an SQLite rowid alias), which is validated at model build.
/// </summary>
public static class SqliteMobileEntityTypeBuilderExtensions
{
    /// <summary>
    /// Adds an R*Tree bounding-box index over a geometry property. Spatial predicates on the property
    /// (<c>Intersects</c>, <c>Contains</c>, <c>Within</c>, <c>Covers</c>, <c>CoveredBy</c>, <c>Touches</c>,
    /// <c>Crosses</c>, <c>Overlaps</c>, <c>EqualsTopologically</c>, <c>IsWithinDistance</c> and
    /// <c>EF.Functions.IsWithinMeters</c>) then pre-filter candidates through the R*Tree before the exact check.
    /// Requires <c>UseShinySpatial()</c>.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasSpatialIndex<TEntity, TGeometry>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, TGeometry?>> property)
        where TEntity : class
        where TGeometry : Geometry
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(property);

        builder.Property(property).HasAnnotation(SidecarAnnotationNames.SpatialIndex, true);
        return builder;
    }

    /// <summary>
    /// Adds a sqlite-vec <c>vec0</c> k-nearest-neighbour index over a <c>float[]</c> embedding, queried with
    /// <c>NearestTo</c>. vec0 keeps the vectors in compact chunks and scans them exactly (it is not an approximate
    /// HNSW/IVF index), which is fast for on-device data sizes. Every stored vector must have exactly <paramref name="dimensions"/> elements (vec0 rejects the
    /// write otherwise). Requires <c>UseShinyVectors()</c>.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasVectorIndex<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, float[]?>> property,
        int dimensions,
        VectorDistanceMetric metric = VectorDistanceMetric.Cosine)
        where TEntity : class
        => SetVector(builder, builder.Property(property), dimensions, metric);

    /// <summary>
    /// Adds a sqlite-vec <c>vec0</c> index over a <c>ReadOnlyMemory&lt;float&gt;</c> embedding. See
    /// <see cref="HasVectorIndex{TEntity}(EntityTypeBuilder{TEntity}, Expression{Func{TEntity, float[]}}, int, VectorDistanceMetric)"/>.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasVectorIndex<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, ReadOnlyMemory<float>>> property,
        int dimensions,
        VectorDistanceMetric metric = VectorDistanceMetric.Cosine)
        where TEntity : class
        => SetVector(builder, builder.Property(property), dimensions, metric);

    /// <summary>
    /// Adds an FTS5 external-content full-text index over one or more string properties, e.g.
    /// <c>e.HasFullTextIndex(x =&gt; new { x.Title, x.Body })</c>. The text is not duplicated: FTS5 reads it from the
    /// entity table. Query with <c>EF.Functions.FullTextMatch</c> / <c>FullTextRank</c>. Requires <c>UseShinyFullText()</c>.
    /// </summary>
    /// <param name="builder">The entity type builder.</param>
    /// <param name="properties">A single property (<c>x =&gt; x.Title</c>) or an anonymous type of properties.</param>
    /// <param name="tokenizer">An FTS5 <c>tokenize=</c> option such as <c>"porter unicode61"</c>; FTS5's default (unicode61) when null.</param>
    public static EntityTypeBuilder<TEntity> HasFullTextIndex<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, object?>> properties,
        string? tokenizer = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(properties);

        var members = properties.GetMemberAccessList();
        if (members.Count == 0)
            throw new ArgumentException("Select at least one property for the full-text index.", nameof(properties));

        foreach (var member in members)
            builder.Property(member.Name);

        builder.HasAnnotation(SidecarAnnotationNames.FullTextIndex, string.Join(",", members.Select(m => m.Name)));
        if (string.IsNullOrWhiteSpace(tokenizer))
            builder.Metadata.RemoveAnnotation(SidecarAnnotationNames.FullTextTokenizer);
        else
            builder.HasAnnotation(SidecarAnnotationNames.FullTextTokenizer, tokenizer);
        return builder;
    }

    static EntityTypeBuilder<TEntity> SetVector<TEntity>(
        EntityTypeBuilder<TEntity> builder,
        PropertyBuilder property,
        int dimensions,
        VectorDistanceMetric metric)
        where TEntity : class
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        if (!Enum.IsDefined(metric))
            throw new ArgumentOutOfRangeException(nameof(metric));

        property
            .HasAnnotation(SidecarAnnotationNames.VectorIndexDimensions, dimensions)
            .HasAnnotation(SidecarAnnotationNames.VectorIndexMetric, metric.ToString());
        return builder;
    }
}
