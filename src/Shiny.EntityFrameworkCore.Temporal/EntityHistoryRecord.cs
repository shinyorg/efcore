namespace Shiny.EntityFrameworkCore.Temporal;

/// <summary>
/// One row of the shared history table. Every entity configured with <c>HasHistory()</c> writes its versions here,
/// so a single table (created by your migrations like any other entity) holds the whole audit trail.
/// </summary>
/// <remarks>
/// The entity is added to the model automatically by <c>HasHistory()</c> with the table name <c>EntityHistory</c>,
/// primary key (<see cref="EntityType"/>, <see cref="EntityKey"/>, <see cref="Version"/>) and indexes on
/// (<see cref="EntityType"/>, <see cref="ValidFrom"/>) and (<see cref="EntityType"/>, <see cref="Actor"/>).
/// Customize it with <c>modelBuilder.Entity&lt;EntityHistoryRecord&gt;()</c> (for example <c>ToTable</c>); your
/// configuration wins over the defaults. Treat the rows as append-only: the library only ever inserts versions,
/// closes the open one, and prunes by the configured retention.
/// </remarks>
public sealed class EntityHistoryRecord
{
    /// <summary>The history name of the entity (<see cref="HistoryOptions.Name"/>, by default the CLR type name).</summary>
    public string EntityType { get; set; } = null!;

    /// <summary>
    /// The entity's primary key as a stable string: the invariant text of a single key value, or a JSON array of
    /// the values for a composite key. Values with a value converter are formatted from their provider value.
    /// </summary>
    public string EntityKey { get; set; } = null!;

    /// <summary>Monotonically increasing version number per entity, starting at 1.</summary>
    public long Version { get; set; }

    /// <summary>When this version became current (UTC, microsecond precision).</summary>
    public DateTime ValidFrom { get; set; }

    /// <summary>When this version stopped being current (UTC); null for the version currently in effect.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>The change that produced this version.</summary>
    public TemporalOperation Operation { get; set; }

    /// <summary>The captured actor, when actor capture is configured.</summary>
    public string? Actor { get; set; }

    /// <summary>
    /// The entity's scalar, complex and owned values as JSON; null for a <see cref="TemporalOperation.Removed"/>
    /// tombstone.
    /// </summary>
    public string? Snapshot { get; set; }
}
