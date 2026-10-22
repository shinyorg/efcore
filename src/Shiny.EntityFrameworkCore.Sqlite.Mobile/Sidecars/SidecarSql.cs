namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

/// <summary>
/// DDL for the sidecar virtual tables and the triggers that keep them in sync with their entity table. Triggers are
/// used (rather than a SaveChanges interceptor) because they run inside the writing statement's transaction and also
/// catch raw SQL, <c>ExecuteUpdate</c> and <c>ExecuteDelete</c>, none of which go through the change tracker.
/// </summary>
static class SidecarSql
{
    public static IEnumerable<string> CreateTable(SidecarDefinition d) => d.Kind switch
    {
        SidecarKind.RTree => [$"CREATE VIRTUAL TABLE IF NOT EXISTS {Q(d.Name)} USING rtree(id, minx, maxx, miny, maxy)"],
        SidecarKind.Vector => [$"CREATE VIRTUAL TABLE IF NOT EXISTS {Q(d.Name)} USING vec0(embedding float[{d.Dimensions}] distance_metric={MetricKeyword(d.Metric)})"],
        SidecarKind.FullText => [CreateFullText(d)],
        _ => throw new ArgumentOutOfRangeException(nameof(d))
    };

    public static IEnumerable<string> DropTriggers(SidecarDefinition d) =>
    [
        $"DROP TRIGGER IF EXISTS {Q(d.Name + "_ai")}",
        $"DROP TRIGGER IF EXISTS {Q(d.Name + "_au")}",
        $"DROP TRIGGER IF EXISTS {Q(d.Name + "_ad")}"
    ];

    public static IEnumerable<string> Drop(SidecarDefinition d) =>
        DropTriggers(d).Append($"DROP TABLE IF EXISTS {Q(d.Name)}");

    /// <summary>(Re)creates the three sync triggers. Idempotent: drops any existing ones first.</summary>
    public static IEnumerable<string> CreateTriggers(SidecarDefinition d) => DropTriggers(d).Concat(d.Kind switch
    {
        SidecarKind.RTree => RTreeTriggers(d),
        SidecarKind.Vector => VectorTriggers(d),
        SidecarKind.FullText => FullTextTriggers(d),
        _ => throw new ArgumentOutOfRangeException(nameof(d))
    });

    /// <summary>Rebuilds the sidecar's contents from the entity table.</summary>
    public static IEnumerable<string> Populate(SidecarDefinition d)
    {
        var t = Q(d.Table);
        var k = Q(d.KeyColumn);
        switch (d.Kind)
        {
            case SidecarKind.RTree:
                var g = Q(d.Columns[0]);
                return
                [
                    $"DELETE FROM {Q(d.Name)}",
                    $"INSERT INTO {Q(d.Name)} (id, minx, maxx, miny, maxy) SELECT {k}, {Bounds(g)} FROM {t} WHERE {g} IS NOT NULL AND {Bound(g, 0)} IS NOT NULL"
                ];

            case SidecarKind.Vector:
                var e = Q(d.Columns[0]);
                return
                [
                    $"DELETE FROM {Q(d.Name)}",
                    $"INSERT INTO {Q(d.Name)} (rowid, embedding) SELECT {k}, {e} FROM {t} WHERE length({e}) > 0"
                ];

            case SidecarKind.FullText:
                return [$"INSERT INTO {Q(d.Name)} ({Q(d.Name)}) VALUES ('rebuild')"];

            default:
                throw new ArgumentOutOfRangeException(nameof(d));
        }
    }

    /// <summary>Everything to stand a sidecar up from nothing: table, triggers, contents.</summary>
    public static IEnumerable<string> Install(SidecarDefinition d) => CreateTable(d).Concat(CreateTriggers(d)).Concat(Populate(d));

    static IEnumerable<string> RTreeTriggers(SidecarDefinition d)
    {
        var s = Q(d.Name);
        var t = Q(d.Table);
        var k = Q(d.KeyColumn);
        var g = Q(d.Columns[0]);
        var insertNew = $"INSERT INTO {s} (id, minx, maxx, miny, maxy) SELECT new.{k}, {Bounds("new." + g)} WHERE new.{g} IS NOT NULL AND {Bound("new." + g, 0)} IS NOT NULL;";

        yield return $"CREATE TRIGGER {Q(d.Name + "_ai")} AFTER INSERT ON {t} BEGIN {insertNew} END";
        yield return $"CREATE TRIGGER {Q(d.Name + "_au")} AFTER UPDATE OF {g}, {k} ON {t} BEGIN DELETE FROM {s} WHERE id = old.{k}; {insertNew} END";
        yield return $"CREATE TRIGGER {Q(d.Name + "_ad")} AFTER DELETE ON {t} BEGIN DELETE FROM {s} WHERE id = old.{k}; END";
    }

    // vec0 does not implement SQLite's conflict resolution (no INSERT OR REPLACE), so updates are delete + insert.
    static IEnumerable<string> VectorTriggers(SidecarDefinition d)
    {
        var s = Q(d.Name);
        var t = Q(d.Table);
        var k = Q(d.KeyColumn);
        var e = Q(d.Columns[0]);
        // NULL and zero-length (a default ReadOnlyMemory<float>) mean "no embedding": the row is stored, not indexed.
        var insertNew = $"INSERT INTO {s} (rowid, embedding) SELECT new.{k}, new.{e} WHERE length(new.{e}) > 0;";

        yield return $"CREATE TRIGGER {Q(d.Name + "_ai")} AFTER INSERT ON {t} BEGIN {insertNew} END";
        yield return $"CREATE TRIGGER {Q(d.Name + "_au")} AFTER UPDATE OF {e}, {k} ON {t} BEGIN DELETE FROM {s} WHERE rowid = old.{k}; {insertNew} END";
        yield return $"CREATE TRIGGER {Q(d.Name + "_ad")} AFTER DELETE ON {t} BEGIN DELETE FROM {s} WHERE rowid = old.{k}; END";
    }

    // The canonical FTS5 external-content triggers (https://sqlite.org/fts5.html#external_content_tables).
    static IEnumerable<string> FullTextTriggers(SidecarDefinition d)
    {
        var s = Q(d.Name);
        var t = Q(d.Table);
        var k = Q(d.KeyColumn);
        var cols = string.Join(", ", d.Columns.Select(Q));
        var newValues = string.Join(", ", d.Columns.Select(c => "new." + Q(c)));
        var oldValues = string.Join(", ", d.Columns.Select(c => "old." + Q(c)));
        var insertNew = $"INSERT INTO {s} (rowid, {cols}) VALUES (new.{k}, {newValues});";
        var deleteOld = $"INSERT INTO {s} ({s}, rowid, {cols}) VALUES ('delete', old.{k}, {oldValues});";

        yield return $"CREATE TRIGGER {Q(d.Name + "_ai")} AFTER INSERT ON {t} BEGIN {insertNew} END";
        yield return $"CREATE TRIGGER {Q(d.Name + "_au")} AFTER UPDATE OF {cols}, {k} ON {t} BEGIN {deleteOld} {insertNew} END";
        yield return $"CREATE TRIGGER {Q(d.Name + "_ad")} AFTER DELETE ON {t} BEGIN {deleteOld} END";
    }

    static string CreateFullText(SidecarDefinition d)
    {
        var args = d.Columns.Select(Q).ToList();
        args.Add($"content={Literal(d.Table)}");
        args.Add($"content_rowid={Literal(d.KeyColumn)}");
        if (!string.IsNullOrWhiteSpace(d.Tokenizer))
            args.Add($"tokenize={Literal(d.Tokenizer)}");

        return $"CREATE VIRTUAL TABLE IF NOT EXISTS {Q(d.Name)} USING fts5({string.Join(", ", args)})";
    }

    static string Bounds(string geometry) =>
        $"{Bound(geometry, 0)}, {Bound(geometry, 1)}, {Bound(geometry, 2)}, {Bound(geometry, 3)}";

    static string Bound(string geometry, int part) => $"{SqliteSpatial.Bound}({geometry}, {part}, 0, 0)";

    public static string MetricKeyword(VectorDistanceMetric metric) => metric switch
    {
        VectorDistanceMetric.Cosine => "cosine",
        VectorDistanceMetric.Euclidean => "l2",
        VectorDistanceMetric.Manhattan => "l1",
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };

    public static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
}
