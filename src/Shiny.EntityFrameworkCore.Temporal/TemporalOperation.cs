namespace Shiny.EntityFrameworkCore.Temporal;

/// <summary>
/// The kind of change that produced a history version.
/// </summary>
public enum TemporalOperation
{
    /// <summary>The entity was added (including a <c>RestoreAsync</c> that re-created a removed entity).</summary>
    Inserted,

    /// <summary>
    /// An existing entity was modified. A change confined to an owned entity or a complex property (including
    /// setting it to null) is recorded as an update of the owning entity, because that is the unit history tracks.
    /// </summary>
    Updated,

    /// <summary>The entity was deleted. The version carries no snapshot (<see cref="EntityVersion{T}.Entity"/> is null).</summary>
    Removed
}
