namespace Shiny.EntityFrameworkCore.Temporal;

/// <summary>
/// A single point-in-time version of an entity, as recorded in the history table.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class EntityVersion<T> where T : class
{
    /// <summary>The entity's key in its stored string form (see <see cref="EntityHistoryRecord.EntityKey"/>).</summary>
    public required string Key { get; init; }

    /// <summary>Monotonically increasing version number, starting at 1.</summary>
    public required long Version { get; init; }

    /// <summary>When this version became the current state of the entity.</summary>
    public required DateTimeOffset ValidFrom { get; init; }

    /// <summary>When this version stopped being current; null for the version currently in effect.</summary>
    public DateTimeOffset? ValidTo { get; init; }

    /// <summary>The change that produced this version.</summary>
    public required TemporalOperation Operation { get; init; }

    /// <summary>The captured actor, when actor capture is configured.</summary>
    public string? Actor { get; init; }

    /// <summary>
    /// The entity state at this version, materialized from the snapshot and <b>not tracked</b> by the context.
    /// Null for <see cref="TemporalOperation.Removed"/> tombstones. Only scalar, complex and owned values are
    /// populated; navigations to other entities are left at their defaults, and shadow properties are not
    /// representable on a detached object (they are still restored by <c>RestoreAsync</c>).
    /// </summary>
    public T? Entity { get; init; }
}
