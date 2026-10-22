using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile;

/// <summary>An entity returned by a nearest-neighbour query, with its distance to the query vector.</summary>
public sealed class VectorMatch<TEntity>
{
    /// <summary>The matched entity (tracked unless the source query is <c>AsNoTracking</c>).</summary>
    public required TEntity Entity { get; init; }

    /// <summary>Distance under the index's metric; smaller is closer.</summary>
    public required double Distance { get; init; }
}

/// <summary>Nearest-neighbour (KNN) queries over a <c>HasVectorIndex</c> vec0 index.</summary>
public static class SqliteMobileQueryableExtensions
{
    static readonly MethodInfo efProperty = typeof(EF).GetMethod(nameof(EF.Property))!;

    /// <summary>
    /// The <paramref name="k"/> entities whose <paramref name="property"/> is nearest to <paramref name="query"/>, using
    /// the property's vec0 index, ordered nearest-first. Compose further (<c>Where</c>, <c>Take</c>, <c>Select</c>) on
    /// the result. Filters applied to <paramref name="source"/> are applied <b>after</b> the k nearest are found, so
    /// a selective filter can return fewer than k rows - over-fetch k in that case.
    /// </summary>
    public static IQueryable<VectorMatch<TEntity>> NearestTo<TEntity>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, float[]?>> property,
        float[] query,
        int k)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(query);
        return Build(source, property, Parameter(new QueryVector<float[]>(query)), SidecarMarkers.VectorMatchArrayMethod, k);
    }

    /// <summary>
    /// The <paramref name="k"/> entities whose <c>ReadOnlyMemory&lt;float&gt;</c> <paramref name="property"/> is nearest to
    /// <paramref name="query"/>. See <see cref="NearestTo{TEntity}(IQueryable{TEntity}, Expression{Func{TEntity, float[]}}, float[], int)"/>.
    /// </summary>
    public static IQueryable<VectorMatch<TEntity>> NearestTo<TEntity>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, ReadOnlyMemory<float>>> property,
        ReadOnlyMemory<float> query,
        int k)
        where TEntity : class
        => Build(source, property, Parameter(new QueryVector<ReadOnlyMemory<float>>(query)), SidecarMarkers.VectorMatchMemoryMethod, k);

    static IQueryable<VectorMatch<TEntity>> Build<TEntity>(
        IQueryable<TEntity> source,
        LambdaExpression property,
        Expression queryValue,
        MethodInfo matchMethod,
        int k)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        var root = QueryRootFinder.Find(source.Expression)
            ?? throw new InvalidOperationException("NearestTo can only be used on an EF Core query (a DbSet or a query composed over one).");
        if (source.Provider is not IAsyncQueryProvider provider)
            throw new InvalidOperationException("NearestTo can only be used on an EF Core query.");

        var model = root.EntityType.Model;
        var entityType = model.FindEntityType(typeof(TEntity)) ?? root.EntityType;
        var member = property.Body is MemberExpression m ? m.Member.Name : throw new ArgumentException("Select a property, e.g. x => x.Embedding.", nameof(property));
        var mapped = entityType.FindProperty(member);
        var sidecarName = mapped?.FindAnnotation(SidecarAnnotationNames.SidecarEntityName)?.Value as string;
        var sidecar = sidecarName == null ? null : model.FindEntityType(sidecarName);
        if (mapped == null || sidecar == null || SidecarModel.VectorDimensions(mapped) == null)
            throw new InvalidOperationException(
                $"'{entityType.DisplayName()}.{member}' has no vector index. Configure it with HasVectorIndex(...) and enable UseShinyVectors().");

        var key = entityType.FindPrimaryKey()!.Properties[0];

        // vec0 KNN: SELECT rowid, distance FROM <vec> WHERE match(@q, embedding) AND k = @k
        var v = Expression.Parameter(sidecar.ClrType, "v");
        var knn = Expression.Call(
            typeof(Queryable), nameof(Queryable.Where), [sidecar.ClrType],
            new EntityQueryRootExpression(provider, sidecar),
            Expression.Quote(Expression.Lambda(
                Expression.AndAlso(
                    Expression.Call(matchMethod, Property(v, typeof(byte[]), "Embedding"), queryValue),
                    Expression.Equal(Property(v, typeof(int), "K"), Parameter(new NeighbourCount(k)))),
                v)));

        var e = Expression.Parameter(typeof(TEntity), "e");
        var v2 = Expression.Parameter(sidecar.ClrType, "v");
        var resultType = typeof(VectorMatch<TEntity>);
        var join = Expression.Call(
            typeof(Queryable), nameof(Queryable.Join), [typeof(TEntity), sidecar.ClrType, key.ClrType, resultType],
            source.Expression,
            knn,
            Expression.Quote(Expression.Lambda(Property(e, key.ClrType, key.Name), e)),
            Expression.Quote(Expression.Lambda(Property(v2, key.ClrType, "RowId"), v2)),
            Expression.Quote(Expression.Lambda(
                Expression.MemberInit(
                    Expression.New(resultType),
                    Expression.Bind(resultType.GetProperty(nameof(VectorMatch<TEntity>.Entity))!, e),
                    Expression.Bind(resultType.GetProperty(nameof(VectorMatch<TEntity>.Distance))!, Property(v2, typeof(double), "Distance"))),
                e, v2)));

        var m2 = Expression.Parameter(resultType, "m");
        var ordered = Expression.Call(
            typeof(Queryable), nameof(Queryable.OrderBy), [resultType, typeof(double)],
            join,
            Expression.Quote(Expression.Lambda(Expression.Property(m2, nameof(VectorMatch<TEntity>.Distance)), m2)));

        return provider.CreateQuery<VectorMatch<TEntity>>(ordered);
    }

    static Expression Property(Expression instance, Type type, string name)
        => Expression.Call(efProperty.MakeGenericMethod(type), instance, Expression.Constant(name));

    // Captured through a closure field so EF turns the value into a SQL parameter (one cached query plan for every
    // query vector) instead of inlining it as a literal. The field name becomes the parameter name.
    static Expression Parameter(object holder)
        => Expression.Field(Expression.Constant(holder), holder.GetType().GetFields()[0]);

    sealed class QueryVector<T>(T query)
    {
        public readonly T Query = query;
    }

    sealed class NeighbourCount(int k)
    {
        public readonly int K = k;
    }

    sealed class QueryRootFinder : ExpressionVisitor
    {
        EntityQueryRootExpression? found;

        public static EntityQueryRootExpression? Find(Expression expression)
        {
            var finder = new QueryRootFinder();
            finder.Visit(expression);
            return finder.found;
        }

        protected override Expression VisitExtension(Expression node)
        {
            if (node is EntityQueryRootExpression root)
            {
                this.found ??= root;
                return node;
            }

            return node.CanReduce ? base.VisitExtension(node) : node;
        }
    }
}
