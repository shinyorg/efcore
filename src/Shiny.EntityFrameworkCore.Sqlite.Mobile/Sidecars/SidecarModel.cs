using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

/// <summary>Reads the index annotations off the model and derives sidecar names and definitions from them.</summary>
static class SidecarModel
{
    public const string EntityNamePrefix = "ShinySidecar:";

    public static string SpatialName(string table, string column) => $"{table}_{column}_rtree";
    public static string VectorName(string table, string column) => $"{table}_{column}_vec";
    public static string FullTextName(string table) => $"{table}_fts";
    public static string EntityName(string sidecarName) => EntityNamePrefix + sidecarName;

    public static bool HasSpatialIndex(IReadOnlyProperty property)
        => property.FindAnnotation(SidecarAnnotationNames.SpatialIndex)?.Value is true;

    public static int? VectorDimensions(IReadOnlyProperty property)
        => property.FindAnnotation(SidecarAnnotationNames.VectorIndexDimensions)?.Value as int?;

    public static VectorDistanceMetric VectorMetric(IReadOnlyProperty property)
        => property.FindAnnotation(SidecarAnnotationNames.VectorIndexMetric)?.Value is string s
            ? Enum.Parse<VectorDistanceMetric>(s)
            : VectorDistanceMetric.Cosine;

    /// <summary>The FTS property names declared on exactly this entity type (not inherited).</summary>
    public static IReadOnlyList<string>? FullTextProperties(IReadOnlyEntityType entityType)
        => entityType.FindAnnotation(SidecarAnnotationNames.FullTextIndex)?.Value is string s
            ? s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

    public static string? Tokenizer(IReadOnlyEntityType entityType)
        => entityType.FindAnnotation(SidecarAnnotationNames.FullTextTokenizer)?.Value as string;

    /// <summary>Finds the entity type (this one or a base) that declares a full-text index.</summary>
    public static IReadOnlyEntityType? FindFullTextOwner(IReadOnlyEntityType entityType)
    {
        var current = entityType;
        while (current != null && FullTextProperties(current) == null)
            current = current.BaseType;
        return current;
    }

    public static bool IsSidecarEntity(IReadOnlyEntityType entityType)
        => entityType.FindAnnotation(SidecarAnnotationNames.SidecarEntity) != null;

    /// <summary>
    /// Definitions for every sidecar whose source columns live in <paramref name="table"/>. This is what the
    /// relational annotation provider turns into table annotations (and therefore into migrations).
    /// </summary>
    public static IEnumerable<SidecarDefinition> For(ITable table)
    {
        if (table.IsExcludedFromMigrations || table.PrimaryKey is not { Columns.Count: 1 } pk)
            yield break;

        var key = pk.Columns[0].Name;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Column lookup through the table's own mappings: this runs while EF is still building the relational model,
        // so IProperty-based lookups (table.FindColumn(property)) would recurse into GetRelationalModel().
        var columns = new Dictionary<IProperty, string>();
        foreach (var c in table.Columns)
        {
            foreach (var mapping in c.PropertyMappings)
                columns[mapping.Property] = c.Name;
        }
        string? ColumnOf(IProperty? property) => property != null && columns.TryGetValue(property, out var name) ? name : null;

        foreach (var entityType in table.EntityTypeMappings.Select(m => m.TypeBase).OfType<IEntityType>())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                var column = ColumnOf(property);
                if (column != null && HasSpatialIndex(property))
                {
                    var name = SpatialName(table.Name, column);
                    if (seen.Add(name))
                        yield return new SidecarDefinition(SidecarKind.RTree, name, table.Name, key, [column]);
                }

                var dims = VectorDimensions(property);
                if (column != null && dims != null)
                {
                    var name = VectorName(table.Name, column);
                    if (seen.Add(name))
                        yield return new SidecarDefinition(SidecarKind.Vector, name, table.Name, key, [column], dims.Value, VectorMetric(property));
                }
            }

            var ftsProperties = FullTextProperties(entityType);
            if (ftsProperties != null)
            {
                var ftsColumns = ftsProperties.Select(n => ColumnOf(entityType.FindProperty(n))).ToList();

                var name = FullTextName(table.Name);
                if (ftsColumns.All(c => c != null) && seen.Add(name))
                    yield return new SidecarDefinition(SidecarKind.FullText, name, table.Name, key, ftsColumns!, tokenizer: Tokenizer(entityType));
            }
        }
    }
}
