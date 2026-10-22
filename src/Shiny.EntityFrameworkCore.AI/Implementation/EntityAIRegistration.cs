using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.AI;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>Frozen, type-erased configuration produced once <c>AddEntity&lt;T&gt;</c> completes.</summary>
abstract class EntityAIRegistration
{
    public required string Slug { get; init; }
    public required EntityAICapabilities Capabilities { get; init; }

    /// <summary>
    /// Services a <c>Where&lt;TService&gt;</c> scope resolves per call — recorded so registration can assert they exist.
    /// </summary>
    public required IReadOnlyList<Type> RequiredServiceTypes { get; init; }

    /// <summary>Validates the registration against the context's EF model and fixes its field set.</summary>
    public abstract IEntityAIBinding Bind(IModel model);
}

/// <summary>A registration resolved against a model: ready to produce tools and to describe itself.</summary>
interface IEntityAIBinding
{
    IReadOnlyList<AIFunction> CreateTools(IDbContextSource source);
    EntityAIDescriptor Describe(IReadOnlyList<string> toolNames);
}

/// <summary>
/// What a host may say about a registration without reaching into its generics — the MCP server's entity and schema
/// resources. Only what the model is already allowed to see: never a scope predicate, never a hidden field.
/// </summary>
sealed record EntityAIDescriptor(
    string Slug,
    string? Description,
    EntityAICapabilities Capabilities,
    JsonElement Schema,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> ToolNames,
    int MaxPageSize,
    bool IsScoped
);

sealed class EntityAIRegistration<T> : EntityAIRegistration where T : class
{
    public string? DescriptionOverride { get; init; }
    public required IReadOnlyDictionary<string, string> PropertyDescriptionOverrides { get; init; }
    public required IReadOnlyList<string>? AllowedPaths { get; init; }
    public required IReadOnlyList<string>? IgnoredPaths { get; init; }
    public required IReadOnlyList<Expression<Func<T, bool>>> Filters { get; init; }
    public required IReadOnlyList<DynamicEntityFilter<T>> DynamicFilters { get; init; }
    public required IReadOnlyList<Action<T, EntityAIFilterContext>> InsertHooks { get; init; }
    public required int MaxPageSize { get; init; }

    public override IEntityAIBinding Bind(IModel model) => new EntityAIBinding<T>(this, model);
}

/// <summary>
/// One entity, resolved against the EF model: its visible fields, its key, the projection the read tools select, and
/// the compiled static scope. Built once; shared by every tool of the entity.
/// </summary>
sealed class EntityAIBinding<T> : IEntityAIBinding where T : class
{
    public EntityAIBinding(EntityAIRegistration<T> registration, IModel model)
    {
        this.Registration = registration;

        var entityType = model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException(
                $"'{typeof(T).Name}' (slug '{registration.Slug}') is not an entity type of this DbContext. Only types the " +
                "EF model maps can be exposed.");

        if (entityType.IsOwned())
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' is an owned type. Owned members are exposed as nested fields of their owner — register the owner instead.");

        this.EntityType = entityType;
        this.Fields = SelectFields(registration, EntityModelReader.ReadFields(entityType));
        this.FieldMap = this.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        this.Description = registration.DescriptionOverride
            ?? EntityModelReader.ReadDescription(entityType, typeof(T));

        this.KeyProperties = ReadKey(entityType);
        this.Key = this.KeyProperties is { Count: 1 }
            ? this.Fields.FirstOrDefault(f => f.IsKey)
            : null;

        this.StaticPredicates = registration.Filters.Select(f => f.Compile()).ToArray();
        this.Projection = BuildProjection(this.Fields);
        this.Validate();
    }

    public EntityAIRegistration<T> Registration { get; }
    public IEntityType EntityType { get; }
    public IReadOnlyList<EntityField> Fields { get; }
    public IReadOnlyDictionary<string, EntityField> FieldMap { get; }
    public string? Description { get; }

    /// <summary>The CLR primary-key properties, in key order — null for a keyless entity or a shadow key.</summary>
    public IReadOnlyList<PropertyInfo>? KeyProperties { get; }

    /// <summary>The single, visible key field the id-based tools address. Null when there is none.</summary>
    public EntityField? Key { get; }

    public Func<T, bool>[] StaticPredicates { get; }

    /// <summary><c>x =&gt; new object[] { x.A, x.Address.City, … }</c> — the read tools select only visible columns.</summary>
    public Expression<Func<T, object?[]>> Projection { get; }

    public bool IsScoped => this.Registration.Filters.Count > 0 || this.Registration.DynamicFilters.Count > 0;

    public string Noun => string.IsNullOrWhiteSpace(this.Description)
        ? $"'{this.Registration.Slug}' records"
        : $"'{this.Registration.Slug}' records ({this.Description})";

    public IReadOnlyList<AIFunction> CreateTools(IDbContextSource source) => EntityAIFunctionFactory.Build(this, source);

    public EntityAIDescriptor Describe(IReadOnlyList<string> toolNames) => new(
        this.Registration.Slug,
        this.Description,
        this.Registration.Capabilities,
        SchemaBuilder.BuildEntitySchema(this.Fields, this.Description),
        this.Fields.Select(f => f.Name).ToArray(),
        toolNames,
        this.Registration.MaxPageSize,
        this.IsScoped);

    void Validate()
    {
        var caps = this.Registration.Capabilities;
        var slug = this.Registration.Slug;
        var byKey = caps & (EntityAICapabilities.Get | EntityAICapabilities.Update | EntityAICapabilities.Delete);

        if (byKey != EntityAICapabilities.None && this.Key is null)
            throw new InvalidOperationException(
                $"'{slug}' asks for {byKey}, which address a row by its id, but '{typeof(T).Name}' has no single visible " +
                "primary-key property (it is keyless, has a composite or shadow key, or the key is hidden with Ignore/AllowProperties).");

        if (caps.HasFlag(EntityAICapabilities.Insert))
        {
            if (this.KeyProperties is null)
                throw new InvalidOperationException($"'{slug}' asks for Insert, but '{typeof(T).Name}' is keyless — EF cannot insert it.");

            var constructible = !typeof(T).IsAbstract
                && typeof(T).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) != null;
            if (!constructible)
                throw new InvalidOperationException(
                    $"'{slug}' asks for Insert, but '{typeof(T).Name}' is abstract or has no parameterless constructor (any visibility).");
        }
    }

    static IReadOnlyList<PropertyInfo>? ReadKey(IEntityType entityType)
    {
        var key = entityType.FindPrimaryKey();
        if (key is null || key.Properties.Any(p => p.PropertyInfo is null))
            return null;
        return key.Properties.Select(p => p.PropertyInfo!).ToArray();
    }

    static IReadOnlyList<EntityField> SelectFields(EntityAIRegistration<T> registration, List<EntityField> all)
    {
        static bool Covers(string path, EntityField field)
            => string.Equals(field.ClrPath, path, StringComparison.Ordinal)
            || field.ClrPath.StartsWith(path + ".", StringComparison.Ordinal);

        // Allow and Property are strict: naming something the model does not expose is almost always a typo or a
        // navigation, and silently doing nothing would hide it. Ignore is lenient — hiding the already-hidden is harmless.
        var configured = (registration.AllowedPaths ?? Array.Empty<string>()).Concat(registration.PropertyDescriptionOverrides.Keys);
        foreach (var path in configured)
        {
            if (!all.Any(f => Covers(path, f)))
                throw new InvalidOperationException(
                    $"'{path}' on '{typeof(T).Name}' is not an exposable field. Only scalar properties of the entity and of " +
                    "its owned/complex members can be exposed — navigations, collections and shadow properties cannot.");
        }

        var visible = all
            .Where(f => registration.AllowedPaths is null || registration.AllowedPaths.Any(p => Covers(p, f)))
            .Where(f => registration.IgnoredPaths is null || !registration.IgnoredPaths.Any(p => Covers(p, f)))
            .ToList();

        if (visible.Count == 0)
            throw new InvalidOperationException(
                $"'{registration.Slug}' exposes no fields. Check AllowProperties/Ignore for '{typeof(T).Name}'.");

        foreach (var field in visible)
        {
            if (registration.PropertyDescriptionOverrides.TryGetValue(field.ClrPath, out var description))
                field.Description = description;
        }

        return visible;
    }

    static Expression<Func<T, object?[]>> BuildProjection(IReadOnlyList<EntityField> fields)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var values = fields.Select(f =>
        {
            Expression access = f.Access(parameter);

            // Lift value types to Nullable<T> first: a column under an optional owned/complex member reads as NULL.
            if (access.Type.IsValueType && Nullable.GetUnderlyingType(access.Type) is null)
                access = Expression.Convert(access, typeof(Nullable<>).MakeGenericType(access.Type));

            return (Expression)Expression.Convert(access, typeof(object));
        });

        return Expression.Lambda<Func<T, object?[]>>(Expression.NewArrayInit(typeof(object), values), parameter);
    }
}
