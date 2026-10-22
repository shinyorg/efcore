using System.Linq.Expressions;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

sealed class EntityAIToolBuilder<T> : IEntityAIToolBuilder<T> where T : class
{
    EntityAICapabilities capabilities = EntityAICapabilities.None;
    string? description;
    readonly Dictionary<string, string> propertyDescriptions = new(StringComparer.Ordinal);
    List<string>? allowed;
    List<string>? ignored;
    List<Expression<Func<T, bool>>>? filters;
    List<DynamicEntityFilter<T>>? dynamicFilters;
    List<Action<T, EntityAIFilterContext>>? insertHooks;
    List<Type>? requiredServices;
    int maxPageSize = 100;

    public IEntityAIToolBuilder<T> Allow(EntityAICapabilities capabilities)
    {
        this.capabilities |= capabilities;
        return this;
    }

    public IEntityAIToolBuilder<T> AllowGet() => this.Allow(EntityAICapabilities.Get);
    public IEntityAIToolBuilder<T> AllowQuery() => this.Allow(EntityAICapabilities.Query);
    public IEntityAIToolBuilder<T> AllowCount() => this.Allow(EntityAICapabilities.Count);
    public IEntityAIToolBuilder<T> AllowAggregate() => this.Allow(EntityAICapabilities.Aggregate);
    public IEntityAIToolBuilder<T> AllowInsert() => this.Allow(EntityAICapabilities.Insert);
    public IEntityAIToolBuilder<T> AllowUpdate() => this.Allow(EntityAICapabilities.Update);
    public IEntityAIToolBuilder<T> AllowDelete() => this.Allow(EntityAICapabilities.Delete);
    public IEntityAIToolBuilder<T> AllowReadOnly() => this.Allow(EntityAICapabilities.ReadOnly);
    public IEntityAIToolBuilder<T> AllowAll() => this.Allow(EntityAICapabilities.All);

    public IEntityAIToolBuilder<T> Description(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        this.description = description;
        return this;
    }

    public IEntityAIToolBuilder<T> Property<TProp>(Expression<Func<T, TProp>> property, string description)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        this.propertyDescriptions[MemberPath.Of(property)] = description;
        return this;
    }

    public IEntityAIToolBuilder<T> AllowProperties(params Expression<Func<T, object?>>[] properties)
    {
        this.allowed ??= new List<string>();
        foreach (var p in properties)
            this.allowed.Add(MemberPath.Of(p));
        return this;
    }

    public IEntityAIToolBuilder<T> Ignore(params Expression<Func<T, object?>>[] properties)
    {
        this.ignored ??= new List<string>();
        foreach (var p in properties)
            this.ignored.Add(MemberPath.Of(p));
        return this;
    }

    public IEntityAIToolBuilder<T> MaxPageSize(int maxPageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPageSize);
        this.maxPageSize = maxPageSize;
        return this;
    }

    public IEntityAIToolBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        this.filters ??= new List<Expression<Func<T, bool>>>();
        this.filters.Add(predicate);
        return this;
    }

    public IEntityAIToolBuilder<T> Where(Func<EntityAIFilterContext, Expression<Func<T, bool>>> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return this.AddDynamic(ctx => new ValueTask<Expression<Func<T, bool>>>(filter(ctx)));
    }

    public IEntityAIToolBuilder<T> Where(Func<EntityAIFilterContext, ValueTask<Expression<Func<T, bool>>>> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return this.AddDynamic(ctx => filter(ctx));
    }

    public IEntityAIToolBuilder<T> Where<TService>(
        Func<TService, EntityAIFilterContext, Expression<Func<T, bool>>> filter) where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(filter);
        this.RequireService<TService>();
        return this.AddDynamic(ctx => new ValueTask<Expression<Func<T, bool>>>(filter(ctx.GetRequiredService<TService>(), ctx)));
    }

    public IEntityAIToolBuilder<T> Where<TService>(
        Func<TService, EntityAIFilterContext, ValueTask<Expression<Func<T, bool>>>> filter) where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(filter);
        this.RequireService<TService>();
        return this.AddDynamic(ctx => filter(ctx.GetRequiredService<TService>(), ctx));
    }

    public IEntityAIToolBuilder<T> OnInsert(Action<T, EntityAIFilterContext> initialize)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        this.insertHooks ??= new List<Action<T, EntityAIFilterContext>>();
        this.insertHooks.Add(initialize);
        return this;
    }

    IEntityAIToolBuilder<T> AddDynamic(DynamicEntityFilter<T> filter)
    {
        this.dynamicFilters ??= new List<DynamicEntityFilter<T>>();
        this.dynamicFilters.Add(filter);
        return this;
    }

    void RequireService<TService>()
    {
        this.requiredServices ??= new List<Type>();
        if (!this.requiredServices.Contains(typeof(TService)))
            this.requiredServices.Add(typeof(TService));
    }

    public EntityAIRegistration<T> Build(string slug) => new()
    {
        Slug = slug,
        Capabilities = this.capabilities == EntityAICapabilities.None ? EntityAICapabilities.ReadOnly : this.capabilities,
        DescriptionOverride = this.description,
        PropertyDescriptionOverrides = this.propertyDescriptions,
        AllowedPaths = this.allowed,
        IgnoredPaths = this.ignored,
        Filters = (IReadOnlyList<Expression<Func<T, bool>>>?)this.filters ?? Array.Empty<Expression<Func<T, bool>>>(),
        DynamicFilters = (IReadOnlyList<DynamicEntityFilter<T>>?)this.dynamicFilters ?? Array.Empty<DynamicEntityFilter<T>>(),
        InsertHooks = (IReadOnlyList<Action<T, EntityAIFilterContext>>?)this.insertHooks ?? Array.Empty<Action<T, EntityAIFilterContext>>(),
        RequiredServiceTypes = (IReadOnlyList<Type>?)this.requiredServices ?? Array.Empty<Type>(),
        MaxPageSize = this.maxPageSize
    };
}

/// <summary>Turns <c>x =&gt; x.Address.City</c> into the CLR member path <c>Address.City</c>.</summary>
static class MemberPath
{
    public static string Of(LambdaExpression expression)
    {
        var body = expression.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            body = unary.Operand;

        var segments = new List<string>();
        while (body is MemberExpression member)
        {
            segments.Add(member.Member.Name);
            body = member.Expression!;
        }

        if (body is not ParameterExpression || segments.Count == 0)
            throw new ArgumentException(
                "Property expression must be a member access chain on the parameter (e.g. x => x.Name or x => x.Address.City).",
                nameof(expression));

        segments.Reverse();
        return string.Join('.', segments);
    }
}
