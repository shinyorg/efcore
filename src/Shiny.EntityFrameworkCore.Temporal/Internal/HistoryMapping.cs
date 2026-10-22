using Microsoft.EntityFrameworkCore.Metadata;

namespace Shiny.EntityFrameworkCore.Temporal.Internal;

/// <summary>The model annotations <c>HasHistory()</c> writes. Plain data only, so the model stays scaffoldable.</summary>
static class HistoryAnnotations
{
    public const string Name = "Shiny:History:Name";
    public const string Retention = "Shiny:History:Retention";
    public const string MaxVersions = "Shiny:History:MaxVersions";
    public const int MaxNameLength = 128;
    public const int MaxKeyLength = 256;
    public const int MaxActorLength = 256;
    public const string DefaultTableName = "EntityHistory";
}

/// <summary>The resolved history configuration of an entity type (the type that called <c>HasHistory()</c>).</summary>
sealed class HistoryMapping
{
    public required IEntityType MappedType { get; init; }
    public required string Name { get; init; }
    public TimeSpan? Retention { get; init; }
    public int? MaxVersions { get; init; }

    /// <summary>
    /// The mapping that governs <paramref name="entityType"/>: its own <c>HasHistory()</c> or that of the top-most
    /// mapped base type, so every type in a hierarchy shares one history stream keyed by the root's name.
    /// </summary>
    public static HistoryMapping? Find(IEntityType entityType)
    {
        IEntityType? mapped = null;
        for (var current = entityType; current != null; current = current.BaseType)
        {
            if (current.FindAnnotation(HistoryAnnotations.Name)?.Value is string)
                mapped = current;
        }
        return mapped == null
            ? null
            : new HistoryMapping
            {
                MappedType = mapped,
                Name = (string)mapped.FindAnnotation(HistoryAnnotations.Name)!.Value!,
                Retention = mapped.FindAnnotation(HistoryAnnotations.Retention)?.Value as TimeSpan?,
                MaxVersions = mapped.FindAnnotation(HistoryAnnotations.MaxVersions)?.Value as int?
            };
    }

    /// <summary>The non-owned entity at the top of an ownership chain (the type whose history an owned change belongs to).</summary>
    public static IEntityType OwnershipRoot(IEntityType entityType)
    {
        var current = entityType;
        while (current.FindOwnership() is { } ownership)
            current = ownership.PrincipalEntityType;
        return current;
    }
}
