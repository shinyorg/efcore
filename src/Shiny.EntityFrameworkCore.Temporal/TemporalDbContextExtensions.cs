using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shiny.EntityFrameworkCore.Temporal.Internal;

namespace Shiny.EntityFrameworkCore.Temporal;

/// <summary>
/// Reads and restores entity history. Every method requires the entity to be configured with <c>HasHistory()</c>
/// and the context to be configured with <c>UseEntityHistory()</c>; otherwise it throws
/// <see cref="InvalidOperationException"/>. Keys are passed the way <c>DbContext.Find</c> takes them: the key value,
/// or an <c>object[]</c> in key order for a composite key.
/// </summary>
/// <remarks>
/// Materialized entities are built from the snapshot and are <b>not tracked</b>. When <typeparamref name="T"/> is a
/// type in an inheritance hierarchy, the history of the whole hierarchy is read and narrowed to instances of
/// <typeparamref name="T"/>.
/// </remarks>
public static class TemporalDbContextExtensions
{
    /// <summary>Every recorded version of one entity, oldest first.</summary>
    public static async Task<IReadOnlyList<EntityVersion<T>>> HistoryAsync<T>(this DbContext context, object key, CancellationToken cancellationToken = default)
        where T : class
    {
        var (mapping, options, entityKey) = Prepare<T>(context, key);
        var rows = await Records(context, mapping)
            .Where(r => r.EntityKey == entityKey)
            .OrderBy(r => r.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToVersions<T>(rows, mapping, options);
    }

    /// <summary>
    /// The entity's state as of <paramref name="asOf"/>: the version whose validity started at or before that
    /// instant and had not yet ended. Null if the entity did not exist yet, or had been removed, at that instant.
    /// </summary>
    public static async Task<T?> AsOfAsync<T>(this DbContext context, object key, DateTimeOffset asOf, CancellationToken cancellationToken = default)
        where T : class
    {
        var (mapping, options, entityKey) = Prepare<T>(context, key);
        var instant = HistoryWriter.Truncate(asOf.UtcDateTime);
        var row = await Records(context, mapping)
            .Where(r => r.EntityKey == entityKey && r.ValidFrom <= instant && (r.ValidTo == null || r.ValidTo > instant))
            .OrderByDescending(r => r.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row?.Snapshot == null ? null : Materialize<T>(row.Snapshot, mapping, options);
    }

    /// <summary>
    /// A point-in-time snapshot of every entity of the type as of <paramref name="asOf"/>. Entities that did not
    /// exist yet, or had been removed, at that instant are excluded.
    /// </summary>
    public static async Task<IReadOnlyList<T>> AsOfAllAsync<T>(this DbContext context, DateTimeOffset asOf, CancellationToken cancellationToken = default)
        where T : class
    {
        var (mapping, options) = Prepare<T>(context);
        var instant = HistoryWriter.Truncate(asOf.UtcDateTime);
        var rows = await Records(context, mapping)
            .Where(r => r.ValidFrom <= instant && (r.ValidTo == null || r.ValidTo > instant) && r.Operation != TemporalOperation.Removed)
            .OrderBy(r => r.EntityKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new List<T>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Snapshot != null && Materialize<T>(row.Snapshot, mapping, options) is { } entity)
                result.Add(entity);
        }
        return result;
    }

    /// <summary>
    /// Every version written by <paramref name="actor"/> across all entities of the type, oldest first - a per-user
    /// audit trail. Requires <see cref="TemporalHistoryOptions.CaptureActor"/> or <see cref="TemporalHistoryOptions.ResolveActor"/>.
    /// </summary>
    public static async Task<IReadOnlyList<EntityVersion<T>>> ChangesByActorAsync<T>(this DbContext context, string actor, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(actor);
        var (mapping, options) = Prepare<T>(context);
        var rows = await Records(context, mapping)
            .Where(r => r.Actor == actor)
            .OrderBy(r => r.ValidFrom).ThenBy(r => r.EntityKey).ThenBy(r => r.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToVersions<T>(rows, mapping, options);
    }

    /// <summary>
    /// Every version whose <see cref="EntityVersion{T}.ValidFrom"/> falls in the half-open window
    /// <c>[from, to)</c>, across all entities of the type, oldest first.
    /// </summary>
    public static async Task<IReadOnlyList<EntityVersion<T>>> ChangesBetweenAsync<T>(this DbContext context, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        where T : class
    {
        if (to < from)
            throw new ArgumentException("'to' must not be earlier than 'from'.", nameof(to));

        var (mapping, options) = Prepare<T>(context);
        var start = Ceiling(from.UtcDateTime);
        var end = Ceiling(to.UtcDateTime);
        var rows = await Records(context, mapping)
            .Where(r => r.ValidFrom >= start && r.ValidFrom < end)
            .OrderBy(r => r.ValidFrom).ThenBy(r => r.EntityKey).ThenBy(r => r.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToVersions<T>(rows, mapping, options);
    }

    /// <summary>
    /// An RFC 6902 patch describing the change from <paramref name="fromVersion"/> to <paramref name="toVersion"/>
    /// of one entity. Paths are JSON Pointers over the snapshot (EF property names; owned and complex values nest).
    /// Null if either version is missing or is a removal tombstone.
    /// </summary>
    public static async Task<JsonPatchDocument<T>?> GetDiffBetweenAsync<T>(this DbContext context, object key, long fromVersion, long toVersion, CancellationToken cancellationToken = default)
        where T : class
    {
        var (mapping, options, entityKey) = Prepare<T>(context, key);
        var from = await Snapshot(context, mapping, entityKey, fromVersion, cancellationToken).ConfigureAwait(false);
        var to = await Snapshot(context, mapping, entityKey, toVersion, cancellationToken).ConfigureAwait(false);
        return from == null || to == null ? null : JsonDiff.CreatePatch<T>(from, to, options.SerializerOptions);
    }

    /// <summary>
    /// Makes <paramref name="version"/> the entity's current state and saves it. History is never rewritten: the
    /// restore is itself recorded as a new version - an update when the entity still exists, or an insert that
    /// re-creates it (with its original key, including identity keys) when it had been removed.
    /// </summary>
    /// <returns>The restored, tracked entity; null if the version doesn't exist or is a removal tombstone.</returns>
    /// <remarks>
    /// This calls <c>SaveChangesAsync</c>, so any other pending changes in the context are saved with it. Store-computed
    /// values (row versions, computed columns) are regenerated by the database; the live concurrency token is kept, so the
    /// restore is a deliberate overwrite rather than a stale-write conflict.
    /// </remarks>
    public static async Task<T?> RestoreAsync<T>(this DbContext context, object key, long version, CancellationToken cancellationToken = default)
        where T : class
    {
        var (mapping, options, entityKey) = Prepare<T>(context, key);
        var json = await Snapshot(context, mapping, entityKey, version, cancellationToken).ConfigureAwait(false);
        if (json == null)
            return null;

        var snapshot = Internal.Snapshot.Read(mapping.MappedType, json, options.SerializerOptions, forInsert: false);
        if (snapshot.Entity is not T)
            return null;

        var pk = mapping.MappedType.FindPrimaryKey()!;
        var keyValues = pk.Properties.Select(p => snapshot.Values[p]).ToArray();
        var live = await context.FindAsync(mapping.MappedType.ClrType, keyValues, cancellationToken).ConfigureAwait(false);

        T restored;
        if (live != null)
        {
            ApplyTo(context.Entry(live), snapshot);
            restored = (T)live;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var insert = Internal.Snapshot.Read(mapping.MappedType, json, options.SerializerOptions, forInsert: true);
            var entry = context.Add(insert.Entity);
            foreach (var (property, value) in insert.Values)
            {
                if (property.IsShadowProperty() && property.GetBeforeSaveBehavior() == PropertySaveBehavior.Save)
                    entry.Property(property.Name).CurrentValue = value;
            }
            restored = (T)insert.Entity;
            await IdentityInsert.SaveAsync(context, insert.EntityType, cancellationToken).ConfigureAwait(false);
        }
        return restored;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    static void ApplyTo(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, Internal.Snapshot.Materialized snapshot)
    {
        foreach (var property in entry.Metadata.GetProperties())
        {
            if (!property.IsPrimaryKey()
                && !property.IsConcurrencyToken
                && property.GetAfterSaveBehavior() == PropertySaveBehavior.Save
                && snapshot.Values.TryGetValue(FindSame(snapshot.EntityType, property), out var value))
            {
                entry.Property(property.Name).CurrentValue = value;
            }
        }

        foreach (var complex in entry.Metadata.GetComplexProperties())
        {
            if (snapshot.EntityType.FindComplexProperty(complex.Name) is { } source)
            {
                var value = Internal.Snapshot.GetMember(source, snapshot.Entity);
                if (complex.IsCollection)
                    entry.ComplexCollection(complex.Name).CurrentValue = (System.Collections.IList?)value;
                else
                    entry.ComplexProperty(complex.Name).CurrentValue = value;
            }
        }

        foreach (var navigation in entry.Metadata.GetNavigations())
        {
            if (navigation.ForeignKey.IsOwnership && !navigation.IsOnDependent && snapshot.EntityType.FindNavigation(navigation.Name) is { } source)
            {
                var value = Internal.Snapshot.GetMember(source, snapshot.Entity);
                if (navigation.IsCollection)
                    entry.Collection(navigation.Name).CurrentValue = (System.Collections.IEnumerable?)value;
                else
                    entry.Reference(navigation.Name).CurrentValue = value;
            }
        }
    }

    // The live entity may be a different type in the hierarchy than the snapshot; match properties by name.
    static IProperty FindSame(IEntityType snapshotType, IProperty property)
        => snapshotType == property.DeclaringType ? property : snapshotType.FindProperty(property.Name) ?? property;

    static IQueryable<EntityHistoryRecord> Records(DbContext context, HistoryMapping mapping)
    {
        var name = mapping.Name;
        return context.Set<EntityHistoryRecord>().AsNoTracking().Where(r => r.EntityType == name);
    }

    static Task<string?> Snapshot(DbContext context, HistoryMapping mapping, string entityKey, long version, CancellationToken ct)
        => Records(context, mapping)
            .Where(r => r.EntityKey == entityKey && r.Version == version)
            .Select(r => r.Snapshot)
            .FirstOrDefaultAsync(ct);

    static (HistoryMapping Mapping, TemporalHistoryOptions Options) Prepare<T>(DbContext context) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = TemporalHistoryOptionsExtension.Find(context)
            ?? throw new InvalidOperationException("Entity history is not enabled on this context. Call UseEntityHistory() on the DbContext options.");
        var entityType = context.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"'{typeof(T).Name}' is not an entity type in this context's model.");
        var mapping = HistoryMapping.Find(entityType)
            ?? throw new InvalidOperationException(
                $"'{entityType.DisplayName()}' does not keep history. Call modelBuilder.Entity<{typeof(T).Name}>().HasHistory() in OnModelCreating.");
        return (mapping, options);
    }

    static (HistoryMapping Mapping, TemporalHistoryOptions Options, string Key) Prepare<T>(DbContext context, object key) where T : class
    {
        var (mapping, options) = Prepare<T>(context);
        return (mapping, options, KeyFormatter.FromUserKey(mapping.MappedType, key));
    }

    static T? Materialize<T>(string json, HistoryMapping mapping, TemporalHistoryOptions options) where T : class
        => Internal.Snapshot.Read(mapping.MappedType, json, options.SerializerOptions, forInsert: false).Entity as T;

    static IReadOnlyList<EntityVersion<T>> ToVersions<T>(List<EntityHistoryRecord> rows, HistoryMapping mapping, TemporalHistoryOptions options) where T : class
    {
        var versions = new List<EntityVersion<T>>(rows.Count);
        foreach (var row in rows)
        {
            T? entity = null;
            var include = true;
            if (row.Snapshot != null)
            {
                entity = Materialize<T>(row.Snapshot, mapping, options);
                // A version of a sibling type in the hierarchy isn't a T.
                include = entity != null;
            }

            if (include)
            {
                versions.Add(new EntityVersion<T>
                {
                    Key = row.EntityKey,
                    Version = row.Version,
                    ValidFrom = AsUtc(row.ValidFrom),
                    ValidTo = row.ValidTo is { } validTo ? AsUtc(validTo) : null,
                    Operation = row.Operation,
                    Actor = row.Actor,
                    Entity = entity
                });
            }
        }
        return versions;
    }

    // Stored instants are whole microseconds, so comparing against the instant rounded the right way is exact on
    // every provider - including SQL Server, which would otherwise round a 100ns parameter to the column's scale.
    // "ValidFrom <= x" holds exactly when "ValidFrom <= truncate(x)"; "ValidFrom >= x" exactly when "ValidFrom >= ceiling(x)".
    static DateTime Ceiling(DateTime utc)
    {
        var truncated = HistoryWriter.Truncate(utc);
        return truncated.Ticks == utc.Ticks ? truncated : truncated.AddTicks(10);
    }

    // Some providers (SQLite, MySQL) hand DateTime back with an unspecified kind; history is always written in UTC.
    static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
