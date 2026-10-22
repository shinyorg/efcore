using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>
/// One LLM-visible field: a scalar column of the entity, or of an owned/complex member flattened to a dotted path
/// (<c>address.city</c>). Everything the tools do with a field — filter, sort, aggregate, project, write — goes through
/// its CLR member chain, so EF translates it exactly as it would a hand-written lambda.
/// </summary>
sealed class EntityField
{
    public required string Name { get; init; }
    public required string ClrPath { get; init; }
    public required IReadOnlyList<PropertyInfo> Chain { get; init; }
    public required Type ClrType { get; init; }
    public required bool IsNullable { get; init; }
    public required bool IsKey { get; init; }
    public required bool InsertWritable { get; init; }
    public required bool UpdateWritable { get; init; }
    public int? MaxLength { get; init; }
    public string? Description { get; set; }

    /// <summary><c>x.Address.City</c> for the given parameter.</summary>
    public Expression Access(Expression parameter)
    {
        var expression = parameter;
        foreach (var property in this.Chain)
            expression = Expression.Property(expression, property);
        return expression;
    }

    /// <summary>Reads the value off a materialized entity; a null owned/complex parent reads as null.</summary>
    public object? GetValue(object entity)
    {
        object? current = entity;
        var i = 0;
        while (current != null && i < this.Chain.Count)
        {
            current = this.Chain[i].GetValue(current);
            i++;
        }
        return current;
    }

    /// <summary>Writes the value, creating owned/complex parents (and writing struct parents back) on the way down.</summary>
    public void SetValue(object entity, object? value) => SetValue(entity, 0, value);

    void SetValue(object target, int index, object? value)
    {
        var property = this.Chain[index];
        if (index == this.Chain.Count - 1)
        {
            property.SetValue(target, value);
            return;
        }

        var child = property.GetValue(target)
            ?? Activator.CreateInstance(property.PropertyType, nonPublic: true)
            ?? throw new InvalidOperationException($"Could not create '{property.PropertyType}' to set '{this.Name}'.");

        this.SetValue(child, index + 1, value);
        property.SetValue(target, child);   // a struct (complex type) parent was boxed: write it back
    }
}

/// <summary>Reads the LLM-visible fields of an entity out of the EF model.</summary>
static class EntityModelReader
{
    const int MaxDepth = 4;
    const string CommentAnnotation = "Relational:Comment";

    /// <summary>
    /// Every supported scalar the model maps on <paramref name="entityType"/>, plus those of its owned references and
    /// complex properties. Shadow and indexer properties, navigations to other entities, collections and types the
    /// structured filter cannot express (blobs, spatial, JSON, value objects) are left out.
    /// </summary>
    public static List<EntityField> ReadFields(IEntityType entityType)
    {
        var fields = new List<EntityField>();
        Walk(entityType, Array.Empty<PropertyInfo>(), "", "", parentNullable: false, isOwned: false, depth: 0, fields);
        return fields;
    }

    public static string? ReadDescription(IReadOnlyAnnotatable annotatable, MemberInfo? member)
    {
        if (member != null)
        {
            var description = member.GetCustomAttribute<DescriptionAttribute>(inherit: true)?.Description;
            if (!string.IsNullOrWhiteSpace(description))
                return description;

            var displayName = member.GetCustomAttribute<DisplayNameAttribute>(inherit: true)?.DisplayName;
            if (!string.IsNullOrWhiteSpace(displayName))
                return displayName;
        }

        return annotatable.FindAnnotation(CommentAnnotation)?.Value as string;
    }

    static void Walk(
        ITypeBase type,
        IReadOnlyList<PropertyInfo> chain,
        string namePrefix,
        string clrPrefix,
        bool parentNullable,
        bool isOwned,
        int depth,
        List<EntityField> fields)
    {
        foreach (var property in type.GetProperties())
        {
            var include = property.PropertyInfo != null
                && !property.IsShadowProperty()
                && !property.IsIndexerProperty()
                && !(isOwned && property.IsKey())          // an owned type's key is the owner's FK
                && ScalarTypes.IsSupported(property.ClrType);

            if (include)
                fields.Add(CreateField(property, chain, namePrefix, clrPrefix, parentNullable, depth == 0));
        }

        if (depth >= MaxDepth)
            return;

        foreach (var complex in type.GetComplexProperties())
        {
            if (!complex.IsCollection && complex.PropertyInfo != null)
            {
                Walk(complex.ComplexType,
                    Append(chain, complex.PropertyInfo),
                    namePrefix + JsonName(complex.PropertyInfo) + ".",
                    clrPrefix + complex.PropertyInfo.Name + ".",
                    parentNullable || complex.IsNullable,
                    isOwned: false,
                    depth + 1,
                    fields);
            }
        }

        if (type is IEntityType entityType)
        {
            foreach (var navigation in entityType.GetNavigations())
            {
                var ownedReference = navigation.ForeignKey.IsOwnership
                    && !navigation.IsOnDependent
                    && !navigation.IsCollection
                    && navigation.PropertyInfo != null;

                if (ownedReference)
                {
                    Walk(navigation.TargetEntityType,
                        Append(chain, navigation.PropertyInfo!),
                        namePrefix + JsonName(navigation.PropertyInfo!) + ".",
                        clrPrefix + navigation.PropertyInfo!.Name + ".",
                        parentNullable || !navigation.ForeignKey.IsRequiredDependent,
                        isOwned: true,
                        depth + 1,
                        fields);
                }
            }
        }
    }

    static EntityField CreateField(
        IProperty property,
        IReadOnlyList<PropertyInfo> chain,
        string namePrefix,
        string clrPrefix,
        bool parentNullable,
        bool isRoot)
    {
        var info = property.PropertyInfo!;
        var isKey = isRoot && property.IsPrimaryKey();
        var computed = (property.ValueGenerated & ValueGenerated.OnUpdate) != 0
            || property.ValueGenerated == ValueGenerated.OnUpdateSometimes;
        var settable = info.SetMethod != null && !computed;

        return new EntityField
        {
            Name = namePrefix + JsonName(info),
            ClrPath = clrPrefix + info.Name,
            Chain = Append(chain, info),
            ClrType = property.ClrType,
            IsNullable = property.IsNullable || parentNullable,
            IsKey = isKey,
            // A store-generated key is the database's to assign; a key the app supplies is the model's to supply.
            InsertWritable = settable && !(isKey && property.ValueGenerated == ValueGenerated.OnAdd),
            // A key identifies the row on update — it is matched, never changed.
            UpdateWritable = settable && !isKey && !property.IsKey(),
            MaxLength = property.GetMaxLength(),
            Description = ReadDescription(property, info)
        };
    }

    static string JsonName(PropertyInfo info)
        => info.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(info.Name);

    static PropertyInfo[] Append(IReadOnlyList<PropertyInfo> chain, PropertyInfo next)
    {
        var result = new PropertyInfo[chain.Count + 1];
        for (var i = 0; i < chain.Count; i++)
            result[i] = chain[i];
        result[^1] = next;
        return result;
    }
}
