namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

/// <summary>
/// Model annotations. The model-level ones (spatial/vector/full-text) are plain strings/ints/bools so they round-trip
/// through the migrations snapshot; the table-level <see cref="SidecarPrefix"/> ones are produced by the relational
/// annotation provider and are what migrations diff.
/// </summary>
static class SidecarAnnotationNames
{
    /// <summary>Property annotation (bool): the property has an R*Tree spatial index.</summary>
    public const string SpatialIndex = "Shiny:SpatialIndex";

    /// <summary>Property annotation (int): the property has a vec0 index with this many dimensions.</summary>
    public const string VectorIndexDimensions = "Shiny:VectorIndexDimensions";

    /// <summary>Property annotation (string): the <see cref="VectorDistanceMetric"/> name of the vec0 index.</summary>
    public const string VectorIndexMetric = "Shiny:VectorIndexMetric";

    /// <summary>Entity annotation (string): comma-separated property names in the FTS5 index.</summary>
    public const string FullTextIndex = "Shiny:FullTextIndex";

    /// <summary>Entity annotation (string): the FTS5 <c>tokenize=</c> option.</summary>
    public const string FullTextTokenizer = "Shiny:FullTextTokenizer";

    /// <summary>Table annotation prefix; the suffix is the sidecar table name, the value a serialized <see cref="SidecarDefinition"/>.</summary>
    public const string SidecarPrefix = "Shiny:Sidecar:";

    /// <summary>Entity annotation on the generated keyless sidecar entity types (value = sidecar kind).</summary>
    public const string SidecarEntity = "Shiny:SidecarEntity";

    /// <summary>
    /// Set by the convention on the indexed property (spatial/vector) or entity type (full-text): the name of the
    /// generated sidecar entity type, so query rewriting never has to re-derive store names from the runtime model.
    /// </summary>
    public const string SidecarEntityName = "Shiny:SidecarEntityName";
}
