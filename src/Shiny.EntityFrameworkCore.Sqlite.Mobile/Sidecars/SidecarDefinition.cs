using System.Text;
using System.Text.Json;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

enum SidecarKind
{
    RTree,
    Vector,
    FullText
}

/// <summary>
/// Everything needed to create, populate, sync and drop one sidecar virtual table, in store terms (table and column
/// names, not CLR names). It is serialized into a table annotation so a migration carries the full definition and
/// can be replayed without the model; any change to it (renamed table/column, new dimensions, different FTS columns)
/// changes the annotation, which is how migrations notice that a sidecar must be rebuilt.
/// </summary>
sealed class SidecarDefinition
{
    public SidecarDefinition(
        SidecarKind kind,
        string name,
        string table,
        string keyColumn,
        IReadOnlyList<string> columns,
        int dimensions = 0,
        VectorDistanceMetric metric = VectorDistanceMetric.Cosine,
        string? tokenizer = null)
    {
        this.Kind = kind;
        this.Name = name;
        this.Table = table;
        this.KeyColumn = keyColumn;
        this.Columns = columns;
        this.Dimensions = dimensions;
        this.Metric = metric;
        this.Tokenizer = tokenizer;
    }

    public SidecarKind Kind { get; }
    public string Name { get; }
    public string Table { get; }
    public string KeyColumn { get; }
    public IReadOnlyList<string> Columns { get; }
    public int Dimensions { get; }
    public VectorDistanceMetric Metric { get; }
    public string? Tokenizer { get; }

    public string AnnotationName => SidecarAnnotationNames.SidecarPrefix + this.Name;

    public string Serialize()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("kind", this.Kind.ToString());
            w.WriteString("name", this.Name);
            w.WriteString("table", this.Table);
            w.WriteString("key", this.KeyColumn);
            w.WriteStartArray("columns");
            foreach (var c in this.Columns)
                w.WriteStringValue(c);
            w.WriteEndArray();
            if (this.Kind == SidecarKind.Vector)
            {
                w.WriteNumber("dimensions", this.Dimensions);
                w.WriteString("metric", this.Metric.ToString());
            }
            if (this.Tokenizer != null)
                w.WriteString("tokenizer", this.Tokenizer);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static SidecarDefinition Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var columns = new List<string>();
        foreach (var c in root.GetProperty("columns").EnumerateArray())
            columns.Add(c.GetString()!);

        return new SidecarDefinition(
            Enum.Parse<SidecarKind>(root.GetProperty("kind").GetString()!),
            root.GetProperty("name").GetString()!,
            root.GetProperty("table").GetString()!,
            root.GetProperty("key").GetString()!,
            columns,
            root.TryGetProperty("dimensions", out var dims) ? dims.GetInt32() : 0,
            root.TryGetProperty("metric", out var metric) ? Enum.Parse<VectorDistanceMetric>(metric.GetString()!) : VectorDistanceMetric.Cosine,
            root.TryGetProperty("tokenizer", out var tok) ? tok.GetString() : null);
    }

    /// <summary>Reads every sidecar definition out of a set of (table or operation) annotations.</summary>
    public static List<SidecarDefinition> From(IEnumerable<Microsoft.EntityFrameworkCore.Infrastructure.IAnnotation> annotations)
    {
        var list = new List<SidecarDefinition>();
        foreach (var a in annotations)
        {
            if (a.Name.StartsWith(SidecarAnnotationNames.SidecarPrefix, StringComparison.Ordinal) && a.Value is string s)
                list.Add(Parse(s));
        }
        return list;
    }
}
