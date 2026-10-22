namespace Shiny.EntityFrameworkCore.Temporal;

/// <summary>
/// Per-entity history settings passed to <see cref="HistoryModelBuilderExtensions.HasHistory{TEntity}"/>.
/// </summary>
/// <remarks>
/// These values are stored as model annotations, so they are plain data (no delegates): that keeps the model
/// scaffoldable by <c>dotnet ef migrations add</c> and compiled models. Behavior that needs code, such as actor
/// capture or the clock, lives on <see cref="TemporalHistoryOptions"/> instead.
/// </remarks>
public sealed class HistoryOptions
{
    /// <summary>
    /// The value written to <see cref="EntityHistoryRecord.EntityType"/> for this entity. Defaults to the entity
    /// type's name (the full CLR name). Set it explicitly when you want history to survive renaming or moving the
    /// class: the history table is matched by this name, not by the CLR type. At most 128 characters.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// When set, closed versions whose validity ended more than this long ago are pruned each time the entity is
    /// written. The current version is never pruned. Null keeps history forever.
    /// </summary>
    public TimeSpan? Retention { get; set; }

    /// <summary>
    /// When set, only the newest N versions of each entity are kept; older ones are pruned each time the entity is
    /// written. Null keeps every version.
    /// </summary>
    public int? MaxVersions { get; set; }
}
