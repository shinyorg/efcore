using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using NetTopologySuite.Geometries;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Spatial;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

/// <summary>
/// Rewrites each query's LINQ tree once, before EF compiles (and caches) it, so the sidecar indexes are used:
/// <list type="bullet">
///   <item>A spatial predicate on an R*Tree-indexed property becomes
///   <c>key IN (SELECT id FROM rtree WHERE &lt;bbox overlap&gt;) AND predicate</c>. Every rewritten predicate implies
///   bounding-box overlap, so the result is identical; SQLite drives the query from the R*Tree and only runs the exact
///   NTS check on the candidates.</item>
///   <item><c>EF.Functions.FullTextMatch</c> becomes <c>key IN (SELECT rowid FROM fts WHERE fts MATCH @q)</c> and
///   <c>FullTextRank</c> a correlated <c>(SELECT rank ...)</c>.</item>
/// </list>
/// The sidecars are addressed as the keyless entity types <see cref="SidecarConvention"/> added to the model, so the
/// result is ordinary LINQ that EF translates, parameterises and composes with everything else in the query.
/// </summary>
sealed class SidecarQueryExpressionInterceptor : IQueryExpressionInterceptor
{
    static readonly ConditionalWeakTable<IModel, object> modelsWithSidecars = new();
    static readonly ConditionalWeakTable<IModel, object> modelsWithout = new();

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        var model = eventData.Context?.Model;
        return model != null && HasSidecars(model)
            ? new SidecarQueryRewriter(model).Visit(queryExpression)
            : queryExpression;
    }

    static bool HasSidecars(IModel model)
    {
        if (modelsWithSidecars.TryGetValue(model, out _))
            return true;
        if (modelsWithout.TryGetValue(model, out _))
            return false;

        var has = model.GetEntityTypes().Any(SidecarModel.IsSidecarEntity);
        (has ? modelsWithSidecars : modelsWithout).AddOrUpdate(model, new object());
        return has;
    }
}

sealed class SidecarQueryRewriter(IModel model) : ExpressionVisitor
{
    static readonly MethodInfo efProperty = typeof(EF).GetMethod(nameof(EF.Property))!;
    static readonly MethodInfo fullTextMatchEntity = typeof(SqliteMobileDbFunctionsExtensions).GetMethods()
        .Single(m => m.Name == nameof(SqliteMobileDbFunctionsExtensions.FullTextMatch) && m.IsGenericMethodDefinition);
    static readonly MethodInfo fullTextMatchProperty = typeof(SqliteMobileDbFunctionsExtensions).GetMethods()
        .Single(m => m.Name == nameof(SqliteMobileDbFunctionsExtensions.FullTextMatch) && !m.IsGenericMethodDefinition);
    static readonly MethodInfo fullTextRank = typeof(SqliteMobileDbFunctionsExtensions).GetMethod(nameof(SqliteMobileDbFunctionsExtensions.FullTextRank))!;
    static readonly MethodInfo concat3 = typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string), typeof(string)])!;

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        var visited = base.VisitMethodCall(node);
        return visited is MethodCallExpression call ? this.Rewrite(call) : visited;
    }

    Expression Rewrite(MethodCallExpression call)
    {
        var method = call.Method;
        var args = call.Arguments;

        if (method.IsGenericMethod && method.GetGenericMethodDefinition() == fullTextMatchEntity)
            return this.FullTextContains(args[1], args[2], null);

        if (method == fullTextMatchProperty)
        {
            var (owner, property) = Member(args[1]) ?? throw new InvalidOperationException(
                "EF.Functions.FullTextMatch(property, query) needs a direct property access such as x.Title.");
            return this.FullTextContains(owner, args[2], property);
        }

        if (method.IsGenericMethod && method.GetGenericMethodDefinition() == fullTextRank)
            return this.FullTextRankSubquery(args[1], args[2]);

        if (call.Object != null && SpatialMethodCallTranslator.ImpliesEnvelopeOverlap(method))
            return this.WithSpatialPrefilter(call, call.Object, args[0], Zero, Zero)
                   ?? this.WithSpatialPrefilter(call, args[0], call.Object, Zero, Zero)
                   ?? call;

        if (call.Object != null && method.GetBaseDefinition() == SpatialMethodCallTranslator.IsWithinDistanceMethod)
            return this.WithSpatialPrefilter(call, call.Object, args[0], args[1], Zero)
                   ?? this.WithSpatialPrefilter(call, args[0], call.Object, args[1], Zero)
                   ?? call;

        if (method == SpatialMethodCallTranslator.IsWithinMetersMethod)
            return this.WithSpatialPrefilter(call, args[1], args[2], Zero, args[3])
                   ?? this.WithSpatialPrefilter(call, args[2], args[1], Zero, args[3])
                   ?? call;

        return call;
    }

    static Expression Zero => Expression.Constant(0d);

    // ── spatial ────────────────────────────────────────────────────────────────────────────────────────────────

    Expression? WithSpatialPrefilter(MethodCallExpression predicate, Expression indexed, Expression other, Expression expand, Expression meters)
    {
        var target = Member(indexed);
        if (target == null)
            return null;

        var (owner, propertyName) = target.Value;
        var entityType = this.EntityTypeOf(owner);
        var property = entityType?.FindProperty(propertyName);
        var sidecar = this.Sidecar(property);
        if (entityType == null || sidecar == null || !SidecarModel.HasSpatialIndex(property!))
            return null;

        var key = entityType.FindPrimaryKey()!.Properties[0];
        var geometry = other.Type == typeof(Geometry) ? other : Expression.Convert(other, typeof(Geometry));

        var r = Expression.Parameter(sidecar.ClrType, "r");
        Expression Bound(int part) => Expression.Call(SidecarMarkers.BoundMethod, geometry, Expression.Constant(part), expand, meters);

        // R*Tree overlap test: box.min <= query.max AND box.max >= query.min on both axes.
        var overlap = Expression.AndAlso(
            Expression.AndAlso(
                Expression.LessThanOrEqual(Property(r, typeof(double), "MinX"), Bound(1)),
                Expression.GreaterThanOrEqual(Property(r, typeof(double), "MaxX"), Bound(0))),
            Expression.AndAlso(
                Expression.LessThanOrEqual(Property(r, typeof(double), "MinY"), Bound(3)),
                Expression.GreaterThanOrEqual(Property(r, typeof(double), "MaxY"), Bound(2))));

        var candidates = this.KeysWhere(sidecar, r, overlap, key.ClrType, "Id");
        var contains = Expression.Call(typeof(Queryable), nameof(Queryable.Contains), [key.ClrType], candidates, Property(owner, key.ClrType, key.Name));
        return Expression.AndAlso(contains, predicate);
    }

    // ── full text ──────────────────────────────────────────────────────────────────────────────────────────────

    Expression FullTextContains(Expression entity, Expression query, string? propertyName)
    {
        var (entityType, sidecar) = this.FullTextSidecar(entity.Type);
        var key = entityType.FindPrimaryKey()!.Properties[0];

        if (propertyName != null)
        {
            var owner = SidecarModel.FindFullTextOwner(entityType)!;
            var indexed = SidecarModel.FullTextProperties(owner)!;
            if (!indexed.Contains(propertyName))
                throw new InvalidOperationException(
                    $"'{entityType.DisplayName()}.{propertyName}' is not part of the full-text index (indexed: {string.Join(", ", indexed)}).");

            var property = entityType.FindProperty(propertyName)!;
            var column = property.GetTableColumnMappings().FirstOrDefault()?.Column.Name ?? propertyName;

            // FTS5 column filter: {"Title"} : (query)
            query = Expression.Call(concat3, Expression.Constant("{\"" + column.Replace("\"", "\"\"") + "\"} : ("), query, Expression.Constant(")"));
        }

        var f = Expression.Parameter(sidecar.ClrType, "f");
        var match = Expression.Call(SidecarMarkers.TextMatchMethod, Property(f, typeof(string), "Match"), query);
        var keys = this.KeysWhere(sidecar, f, match, key.ClrType, "RowId");
        return Expression.Call(typeof(Queryable), nameof(Queryable.Contains), [key.ClrType], keys, Property(entity, key.ClrType, key.Name));
    }

    Expression FullTextRankSubquery(Expression entity, Expression query)
    {
        var (entityType, sidecar) = this.FullTextSidecar(entity.Type);
        var key = entityType.FindPrimaryKey()!.Properties[0];

        var f = Expression.Parameter(sidecar.ClrType, "f");
        var predicate = Expression.AndAlso(
            Expression.Call(SidecarMarkers.TextMatchMethod, Property(f, typeof(string), "Match"), query),
            Expression.Equal(Property(f, key.ClrType, "RowId"), Property(entity, key.ClrType, key.Name)));

        var where = Expression.Call(
            typeof(Queryable), nameof(Queryable.Where), [sidecar.ClrType],
            new EntityQueryRootExpression(sidecar),
            Expression.Quote(Expression.Lambda(predicate, f)));

        var f2 = Expression.Parameter(sidecar.ClrType, "f");
        var rank = Expression.Call(
            typeof(Queryable), nameof(Queryable.Select), [sidecar.ClrType, typeof(double?)],
            where,
            Expression.Quote(Expression.Lambda(Expression.Convert(Property(f2, typeof(double), "Rank"), typeof(double?)), f2)));

        return Expression.Call(typeof(Queryable), nameof(Queryable.FirstOrDefault), [typeof(double?)], rank);
    }

    (IEntityType EntityType, IEntityType Sidecar) FullTextSidecar(Type clrType)
    {
        var entityType = model.FindEntityType(clrType)
            ?? throw new InvalidOperationException($"'{clrType.Name}' is not an entity type in this model, so it has no full-text index.");

        var owner = SidecarModel.FindFullTextOwner(entityType);
        var sidecarName = owner?.FindAnnotation(SidecarAnnotationNames.SidecarEntityName)?.Value as string;
        var sidecar = sidecarName == null ? null : model.FindEntityType(sidecarName);
        return sidecar == null
            ? throw new InvalidOperationException(
                $"'{entityType.DisplayName()}' has no full-text index. Configure it with HasFullTextIndex(...) and enable UseShinyFullText().")
            : (entityType, sidecar);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────

    Expression KeysWhere(IEntityType sidecar, ParameterExpression row, Expression predicate, Type keyType, string keyProperty)
    {
        var where = Expression.Call(
            typeof(Queryable), nameof(Queryable.Where), [sidecar.ClrType],
            new EntityQueryRootExpression(sidecar),
            Expression.Quote(Expression.Lambda(predicate, row)));

        var r = Expression.Parameter(sidecar.ClrType, row.Name);
        return Expression.Call(
            typeof(Queryable), nameof(Queryable.Select), [sidecar.ClrType, keyType],
            where,
            Expression.Quote(Expression.Lambda(Property(r, keyType, keyProperty), r)));
    }

    IEntityType? EntityTypeOf(Expression owner) => model.FindEntityType(owner.Type);

    IEntityType? Sidecar(IProperty? property)
        => property?.FindAnnotation(SidecarAnnotationNames.SidecarEntityName)?.Value is string name
            ? model.FindEntityType(name)
            : null;

    /// <summary>Recognises <c>owner.Property</c> and <c>EF.Property&lt;T&gt;(owner, "Property")</c>, through casts.</summary>
    static (Expression Owner, string Property)? Member(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.TypeAs } unary)
            expression = unary.Operand;

        if (expression is MemberExpression { Expression: not null } member)
            return (member.Expression, member.Member.Name);

        if (expression is MethodCallExpression { Method.IsGenericMethod: true } call
            && call.Method.GetGenericMethodDefinition() == efProperty
            && call.Arguments[1] is ConstantExpression { Value: string name })
            return (call.Arguments[0], name);

        return null;
    }

    static Expression Property(Expression instance, Type type, string name)
        => Expression.Call(efProperty.MakeGenericMethod(type), instance, Expression.Constant(name));
}
