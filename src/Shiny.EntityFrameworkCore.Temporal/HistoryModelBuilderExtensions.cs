using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shiny.EntityFrameworkCore.Temporal.Internal;

namespace Shiny.EntityFrameworkCore.Temporal;

/// <summary>
/// Model-building extensions that opt an entity into provider-agnostic history.
/// </summary>
public static class HistoryModelBuilderExtensions
{
    /// <summary>
    /// Records an append-only version of this entity on every insert, update and delete saved through the change
    /// tracker, into the shared <see cref="EntityHistoryRecord"/> table (added to the model so migrations create
    /// it). Derived types share the history of the type that calls this. Requires
    /// <c>UseEntityHistory()</c> on the context options, which installs the interceptor that does the writing.
    /// </summary>
    /// <remarks>
    /// Named <c>HasHistory</c> rather than <c>IsTemporal</c> so it doesn't collide with SQL Server's system-versioned
    /// temporal tables, which this feature does not use: it works the same on every relational provider.
    /// </remarks>
    public static EntityTypeBuilder<TEntity> HasHistory<TEntity>(this EntityTypeBuilder<TEntity> builder, Action<HistoryOptions>? configure = null)
        where TEntity : class
    {
        HasHistory((EntityTypeBuilder)builder, configure);
        return builder;
    }

    /// <inheritdoc cref="HasHistory{TEntity}(EntityTypeBuilder{TEntity}, Action{HistoryOptions}?)"/>
    public static EntityTypeBuilder HasHistory(this EntityTypeBuilder builder, Action<HistoryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var entityType = builder.Metadata;

        if (entityType.ClrType == typeof(EntityHistoryRecord))
            throw new InvalidOperationException("The history table itself can't keep history.");
        if (entityType.IsKeyless)
            throw new InvalidOperationException($"'{entityType.DisplayName()}' is keyless; history needs a primary key to identify versions.");
        for (var baseType = entityType.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (baseType.FindAnnotation(HistoryAnnotations.Name) != null)
                throw new InvalidOperationException(
                    $"'{entityType.DisplayName()}' already keeps history through its base type '{baseType.DisplayName()}'; derived types share the base type's history.");
        }

        var options = new HistoryOptions();
        configure?.Invoke(options);

        var name = options.Name ?? entityType.Name;
        if (String.IsNullOrWhiteSpace(name))
            throw new ArgumentException("History name can't be empty.", nameof(configure));
        if (name.Length > HistoryAnnotations.MaxNameLength)
            throw new ArgumentException(
                $"History name '{name}' is longer than {HistoryAnnotations.MaxNameLength} characters; set HistoryOptions.Name to a shorter, stable name.",
                nameof(configure));
        if (options.Retention is { } retention && retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configure), retention, "Retention must be positive.");
        if (options.MaxVersions is { } max && max < 1)
            throw new ArgumentOutOfRangeException(nameof(configure), max, "MaxVersions must be at least 1.");

        entityType.SetAnnotation(HistoryAnnotations.Name, name);
        SetOrRemove(entityType, HistoryAnnotations.Retention, options.Retention);
        SetOrRemove(entityType, HistoryAnnotations.MaxVersions, options.MaxVersions);

        AddHistoryEntity(entityType.Model);
        return builder;
    }

    static void SetOrRemove(IMutableEntityType entityType, string name, object? value)
    {
        if (value == null)
            entityType.RemoveAnnotation(name);
        else
            entityType.SetAnnotation(name, value);
    }

    // Configured through the convention builder so anything the user sets explicitly on
    // modelBuilder.Entity<EntityHistoryRecord>() - before or after HasHistory() - wins. The entity type itself is
    // added with data-annotation strength: a convention-strength entity that no navigation or DbSet reaches is
    // removed again by EF's model cleanup.
    static void AddHistoryEntity(IMutableModel model)
    {
        var modelBuilder = ((IConventionModel)model).Builder;
        var entity = modelBuilder.Entity(typeof(EntityHistoryRecord), fromDataAnnotation: true)
            ?? throw new InvalidOperationException($"'{nameof(EntityHistoryRecord)}' was explicitly ignored in the model; history needs it.");

        entity.ToTable(HistoryAnnotations.DefaultTableName);
        entity.PrimaryKey([nameof(EntityHistoryRecord.EntityType), nameof(EntityHistoryRecord.EntityKey), nameof(EntityHistoryRecord.Version)]);

        entity.Property(typeof(string), nameof(EntityHistoryRecord.EntityType))!.HasMaxLength(HistoryAnnotations.MaxNameLength);
        entity.Property(typeof(string), nameof(EntityHistoryRecord.EntityKey))!.HasMaxLength(HistoryAnnotations.MaxKeyLength);
        entity.Property(typeof(string), nameof(EntityHistoryRecord.Actor))!.HasMaxLength(HistoryAnnotations.MaxActorLength);
        entity.Property(typeof(long), nameof(EntityHistoryRecord.Version))!.ValueGenerated(ValueGenerated.Never);
        entity.Property(typeof(DateTime), nameof(EntityHistoryRecord.ValidFrom))!.HasPrecision(6);
        entity.Property(typeof(DateTime?), nameof(EntityHistoryRecord.ValidTo))!.HasPrecision(6);

        var operation = entity.Property(typeof(TemporalOperation), nameof(EntityHistoryRecord.Operation))!;
        operation.HasConversion(typeof(string));
        operation.HasMaxLength(16);

        entity.HasIndex([nameof(EntityHistoryRecord.EntityType), nameof(EntityHistoryRecord.ValidFrom)]);
        entity.HasIndex([nameof(EntityHistoryRecord.EntityType), nameof(EntityHistoryRecord.Actor)]);
    }
}
