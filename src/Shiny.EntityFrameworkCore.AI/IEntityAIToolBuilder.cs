using System.Linq.Expressions;

namespace Shiny.EntityFrameworkCore.AI;

/// <summary>
/// Per-entity configuration for AI tool exposure: which operations, which rows (the scope), which fields, and how
/// the entity is described to the model.
/// </summary>
public interface IEntityAIToolBuilder<T> where T : class
{
    // ── Capabilities ────────────────────────────────────────────────────

    /// <summary>Adds the given capabilities. With no <c>Allow*</c> call at all the entity is <see cref="EntityAICapabilities.ReadOnly"/>.</summary>
    IEntityAIToolBuilder<T> Allow(EntityAICapabilities capabilities);

    /// <summary>Adds <see cref="EntityAICapabilities.Get"/> — <c>{entity}_get_by_id</c>.</summary>
    IEntityAIToolBuilder<T> AllowGet();

    /// <summary>Adds <see cref="EntityAICapabilities.Query"/> — <c>{entity}_query</c>.</summary>
    IEntityAIToolBuilder<T> AllowQuery();

    /// <summary>Adds <see cref="EntityAICapabilities.Count"/> — <c>{entity}_count</c>.</summary>
    IEntityAIToolBuilder<T> AllowCount();

    /// <summary>Adds <see cref="EntityAICapabilities.Aggregate"/> — <c>{entity}_aggregate</c>.</summary>
    IEntityAIToolBuilder<T> AllowAggregate();

    /// <summary>Adds <see cref="EntityAICapabilities.Insert"/> — <c>{entity}_insert</c>.</summary>
    IEntityAIToolBuilder<T> AllowInsert();

    /// <summary>Adds <see cref="EntityAICapabilities.Update"/> — <c>{entity}_update</c>.</summary>
    IEntityAIToolBuilder<T> AllowUpdate();

    /// <summary>Adds <see cref="EntityAICapabilities.Delete"/> — <c>{entity}_delete</c>.</summary>
    IEntityAIToolBuilder<T> AllowDelete();

    /// <summary>Adds <see cref="EntityAICapabilities.ReadOnly"/> (get, query, count, aggregate).</summary>
    IEntityAIToolBuilder<T> AllowReadOnly();

    /// <summary>Adds <see cref="EntityAICapabilities.All"/>.</summary>
    IEntityAIToolBuilder<T> AllowAll();

    // ── Shape ───────────────────────────────────────────────────────────

    /// <summary>
    /// Describes the entity in the generated tool descriptions and JSON schema. Defaults to a <c>[Description]</c>
    /// on the class, then the table comment from the EF model (<c>HasComment</c>).
    /// </summary>
    IEntityAIToolBuilder<T> Description(string description);

    /// <summary>
    /// Describes one field. Defaults to a <c>[Description]</c> on the property, then the column comment from the EF
    /// model. Nested owned/complex members are addressed with a member chain (<c>o =&gt; o.Address.City</c>).
    /// </summary>
    IEntityAIToolBuilder<T> Property<TProp>(Expression<Func<T, TProp>> property, string description);

    /// <summary>
    /// Restricts the fields the model can see, filter, sort and write to the ones listed. Naming an owned or complex
    /// member includes everything under it. If never called, every supported scalar the EF model maps is exposed.
    /// </summary>
    IEntityAIToolBuilder<T> AllowProperties(params Expression<Func<T, object?>>[] properties);

    /// <summary>
    /// Hides fields from the model entirely: absent from every schema, refused as a filter/sort/aggregate field,
    /// refused in an insert/update body, and never selected from the database by the read tools.
    /// </summary>
    IEntityAIToolBuilder<T> Ignore(params Expression<Func<T, object?>>[] properties);

    /// <summary>Caps the page size the model can request from the query tool. Default 100.</summary>
    IEntityAIToolBuilder<T> MaxPageSize(int maxPageSize);

    // ── The scope ───────────────────────────────────────────────────────

    /// <summary>
    /// Registers a fixed, server-side predicate that constrains <b>every</b> tool for this entity. The model cannot
    /// see it, remove it or widen it — it is AND-applied to the <see cref="IQueryable{T}"/> before the model's own
    /// filter, so it is part of the SQL. Call more than once to require several conditions.
    /// </summary>
    /// <remarks>
    /// query/count/aggregate execute it in SQL; get-by-id, update and delete find their row <b>through</b> it, so an
    /// out-of-scope key is indistinguishable from a missing one; insert and update evaluate it in memory against the
    /// entity about to be saved and refuse anything that would fall outside it. Keep it to members of the entity
    /// itself (and its owned/complex members) — a predicate that walks a navigation is not loaded in memory, so the
    /// write-side check would refuse every write.
    /// </remarks>
    IEntityAIToolBuilder<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>
    /// Registers a scope predicate <b>resolved per tool call</b> from the call's services — the answer to "which rows
    /// may this caller see" when it lives in a request-scoped service. AND-combined with every other predicate and
    /// still invisible to the model.
    /// </summary>
    /// <remarks>
    /// Fails <b>closed</b>: if the callback throws, returns null, or the call carries no
    /// <see cref="global::Microsoft.Extensions.AI.AIFunctionArguments.Services"/>, the tool call fails. It never
    /// degrades into running without the predicate.
    /// </remarks>
    IEntityAIToolBuilder<T> Where(Func<EntityAIFilterContext, Expression<Func<T, bool>>> filter);

    /// <summary>Asynchronous <see cref="Where(Func{EntityAIFilterContext, Expression{Func{T, bool}}})"/> — for a scope the service has to go and look up.</summary>
    IEntityAIToolBuilder<T> Where(Func<EntityAIFilterContext, ValueTask<Expression<Func<T, bool>>>> filter);

    /// <summary>
    /// Registers a scope predicate built from a service resolved out of the call's scope (<c>GetRequiredService</c>).
    /// The common form: <c>Where&lt;ITenantContext&gt;((t, _) =&gt; o =&gt; o.TenantId == t.TenantId)</c>. The service
    /// must be registered before the tools are — that is checked at startup.
    /// </summary>
    IEntityAIToolBuilder<T> Where<TService>(
        Func<TService, EntityAIFilterContext, Expression<Func<T, bool>>> filter) where TService : notnull;

    /// <summary>Asynchronous <see cref="Where{TService}(Func{TService, EntityAIFilterContext, Expression{Func{T, bool}}})"/>.</summary>
    IEntityAIToolBuilder<T> Where<TService>(
        Func<TService, EntityAIFilterContext, ValueTask<Expression<Func<T, bool>>>> filter) where TService : notnull;

    /// <summary>
    /// Runs on every entity the insert tool builds, after the model's values are applied and <b>before</b> the scope
    /// check — the place to stamp server-owned values the model is not allowed to set (a hidden <c>TenantId</c>, an
    /// owner id, a created-by). Call more than once to chain hooks.
    /// </summary>
    /// <remarks>
    /// The scope check still runs afterwards, so a hook cannot be used to smuggle a row out of scope. The context's
    /// <see cref="EntityAIFilterContext.Services"/> is the call's services when the call carries them, otherwise the
    /// service scope the tool opened for its <c>DbContext</c>.
    /// </remarks>
    IEntityAIToolBuilder<T> OnInsert(Action<T, EntityAIFilterContext> initialize);
}
