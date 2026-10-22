using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Shiny.EntityFrameworkCore.Temporal.Internal;

/// <summary>
/// Writes and reads history snapshots. A snapshot is built from EF's own metadata and change-tracker values -
/// scalar properties (including shadow ones), complex properties, and owned navigations, recursively - and never
/// from serializing the CLR object graph. That makes it cycle-free (navigations to other entities are not
/// followed), independent of JSON attributes on the entity, and able to carry shadow properties.
/// </summary>
static class Snapshot
{
    /// <summary>Discriminator written when the entry's type is a derived type of the history-mapped type.</summary>
    public const string TypeProperty = "$type";

    public static string Write(EntityEntry entry, IEntityType mappedType, JsonSerializerOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteEntity(writer, entry, null, entry.Metadata != mappedType ? entry.Metadata.Name : null, options);
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteEntity(Utf8JsonWriter writer, EntityEntry entry, IForeignKey? ownership, string? typeTag, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (typeTag != null)
            writer.WriteString(TypeProperty, typeTag);

        foreach (var property in entry.Metadata.GetProperties())
        {
            // Owned values are identified by their owner; their ownership FK and synthesized shadow keys are
            // plumbing, not state, and would make two otherwise-identical snapshots differ.
            if (ownership == null || (!property.IsShadowProperty() && !ownership.Properties.Contains(property)))
            {
                writer.WritePropertyName(property.Name);
                JsonSerializer.Serialize(writer, entry.Property(property.Name).CurrentValue, property.ClrType, options);
            }
        }

        foreach (var complex in entry.Metadata.GetComplexProperties())
        {
            writer.WritePropertyName(complex.Name);
            WriteComplex(writer, complex, GetMember(complex, entry.Entity), options);
        }

        foreach (var navigation in entry.Metadata.GetNavigations())
        {
            if (navigation.ForeignKey.IsOwnership && !navigation.IsOnDependent)
            {
                writer.WritePropertyName(navigation.Name);
                if (navigation.IsCollection)
                    WriteOwnedCollection(writer, entry.Collection(navigation.Name), navigation, options);
                else
                    WriteOwnedReference(writer, entry.Reference(navigation.Name).TargetEntry, navigation, options);
            }
        }

        writer.WriteEndObject();
    }

    static void WriteOwnedReference(Utf8JsonWriter writer, EntityEntry? target, INavigation navigation, JsonSerializerOptions options)
    {
        if (target == null || target.State is EntityState.Deleted or EntityState.Detached)
            writer.WriteNullValue();
        else
            WriteEntity(writer, target, navigation.ForeignKey, null, options);
    }

    static void WriteOwnedCollection(Utf8JsonWriter writer, CollectionEntry collection, INavigation navigation, JsonSerializerOptions options)
    {
        if (collection.CurrentValue == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in collection.CurrentValue)
        {
            var target = collection.FindEntry(item);
            if (target != null && target.State is not (EntityState.Deleted or EntityState.Detached))
                WriteEntity(writer, target, navigation.ForeignKey, null, options);
        }
        writer.WriteEndArray();
    }

    static void WriteComplex(Utf8JsonWriter writer, IComplexProperty complex, object? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
        }
        else if (complex.IsCollection)
        {
            writer.WriteStartArray();
            foreach (var item in (System.Collections.IEnumerable)value)
                WriteComplexObject(writer, complex.ComplexType, item, options);
            writer.WriteEndArray();
        }
        else
        {
            WriteComplexObject(writer, complex.ComplexType, value, options);
        }
    }

    static void WriteComplexObject(Utf8JsonWriter writer, IComplexType type, object? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (var property in type.GetProperties())
        {
            writer.WritePropertyName(property.Name);
            JsonSerializer.Serialize(writer, GetMember(property, value), property.ClrType, options);
        }
        foreach (var nested in type.GetComplexProperties())
        {
            writer.WritePropertyName(nested.Name);
            WriteComplex(writer, nested, GetMember(nested, value), options);
        }
        writer.WriteEndObject();
    }

    // ── Reading ──────────────────────────────────────────────────────────

    /// <summary>A materialized snapshot: the detached entity, its concrete entity type, and every root scalar value (shadow ones included).</summary>
    public sealed record Materialized(object Entity, IEntityType EntityType, IReadOnlyDictionary<IProperty, object?> Values);

    /// <param name="mappedType">The history-mapped type the snapshot was written under.</param>
    /// <param name="json">The snapshot.</param>
    /// <param name="options">Serializer options for scalar values.</param>
    /// <param name="forInsert">
    /// True when the object will be added to the context (a restore of a removed entity): store-computed values
    /// (row versions, computed columns) are left at their defaults so the database generates them again.
    /// </param>
    public static Materialized Read(IEntityType mappedType, string json, JsonSerializerOptions options, bool forInsert)
    {
        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("History snapshot is not a JSON object.");

        var entityType = mappedType;
        if (node[TypeProperty]?.GetValue<string>() is { } typeName)
        {
            entityType = mappedType.Model.FindEntityType(typeName)
                ?? throw new InvalidOperationException($"History snapshot refers to entity type '{typeName}', which is no longer in the model.");
        }

        var values = new Dictionary<IProperty, object?>();
        var entity = ReadStructural(entityType, node, options, values, forInsert);
        return new Materialized(entity, entityType, values);
    }

    static object ReadStructural(ITypeBase type, JsonObject node, JsonSerializerOptions options, Dictionary<IProperty, object?>? values, bool forInsert)
    {
        var instance = CreateInstance(type.ClrType);

        foreach (var property in type.GetProperties())
        {
            if (node.TryGetPropertyValue(property.Name, out var valueNode))
            {
                var value = valueNode?.Deserialize(property.ClrType, options);
                values?.Add(property, value);

                var settable = !property.IsShadowProperty()
                    && (value != null || !property.ClrType.IsValueType || Nullable.GetUnderlyingType(property.ClrType) != null)
                    && (!forInsert || property.IsPrimaryKey() || property.GetBeforeSaveBehavior() == PropertySaveBehavior.Save);
                if (settable)
                    SetMember(property, instance, value);
            }
        }

        foreach (var complex in type.GetComplexProperties())
        {
            if (node.TryGetPropertyValue(complex.Name, out var complexNode))
                ReadComplex(complex, instance, complexNode, options);
        }

        if (type is IEntityType entityType)
        {
            foreach (var navigation in entityType.GetNavigations())
            {
                if (navigation.ForeignKey.IsOwnership && !navigation.IsOnDependent && node.TryGetPropertyValue(navigation.Name, out var ownedNode))
                    ReadOwned(navigation, instance, ownedNode, options, forInsert);
            }
        }

        return instance;
    }

    static void ReadOwned(INavigation navigation, object owner, JsonNode? node, JsonSerializerOptions options, bool forInsert)
    {
        if (node == null)
        {
            SetMember(navigation, owner, null);
        }
        else if (navigation.IsCollection)
        {
            var accessor = navigation.GetCollectionAccessor()!;
            accessor.GetOrCreate(owner, forMaterialization: true);
            foreach (var item in node.AsArray())
            {
                if (item is JsonObject obj)
                    accessor.Add(owner, ReadStructural(navigation.TargetEntityType, obj, options, null, forInsert), forMaterialization: true);
            }
        }
        else
        {
            SetMember(navigation, owner, ReadStructural(navigation.TargetEntityType, node.AsObject(), options, null, forInsert));
        }
    }

    static void ReadComplex(IComplexProperty complex, object owner, JsonNode? node, JsonSerializerOptions options)
    {
        if (node == null)
        {
            if (complex.IsNullable)
                SetMember(complex, owner, null);
        }
        else if (complex.IsCollection)
        {
            var accessor = complex.GetCollectionAccessor()!;
            var collection = accessor.Create();
            foreach (var item in node.AsArray())
            {
                if (item is JsonObject obj)
                    accessor.AddStandalone(collection, ReadStructural(complex.ComplexType, obj, options, null, false));
            }
            SetMember(complex, owner, collection);
        }
        else
        {
            SetMember(complex, owner, ReadStructural(complex.ComplexType, node.AsObject(), options, null, false));
        }
    }

    // ── Member access ────────────────────────────────────────────────────

    /// <summary>Reads a CLR-backed member the way EF does (property, or its configured backing field).</summary>
    public static object? GetMember(IPropertyBase property, object instance)
        => property.GetMemberInfo(forMaterialization: false, forSet: false) switch
        {
            PropertyInfo pi => pi.GetValue(instance),
            FieldInfo fi => fi.GetValue(instance),
            var m => throw new InvalidOperationException($"Unsupported member '{m}' for '{property.Name}'.")
        };

    static void SetMember(IPropertyBase property, object instance, object? value)
    {
        switch (property.GetMemberInfo(forMaterialization: true, forSet: true))
        {
            case PropertyInfo pi:
                pi.SetValue(instance, value);
                break;
            case FieldInfo fi:
                fi.SetValue(instance, value);
                break;
            default:
                throw new InvalidOperationException($"Can't set '{property.Name}' on '{instance.GetType().Name}'.");
        }
    }

    static object CreateInstance(Type type)
    {
        if (type.IsValueType)
            return Activator.CreateInstance(type)!;

        var ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
        // Constructor-bound entities without a parameterless constructor are created uninitialized; every mapped
        // member is then set from the snapshot, which is what EF's own materializer guarantees anyway.
        return ctor != null ? ctor.Invoke(null) : RuntimeHelpers.GetUninitializedObject(type);
    }
}
