namespace Shiny.EntityFrameworkCore.AI;

/// <summary>
/// Registers the entities of one <c>DbContext</c> that an LLM is allowed to reach. An entity that is not registered
/// here is invisible to the model — there is no "expose everything" switch.
/// </summary>
public interface IDbContextAIToolBuilder
{
    /// <summary>
    /// Exposes entity <typeparamref name="T"/> as a set of AI tools. Its fields, types, keys and nullability come
    /// from the context's EF model, not from reflection over the CLR type.
    /// </summary>
    /// <typeparam name="T">An entity type mapped by the context (it must be in the EF model).</typeparam>
    /// <param name="configure">
    /// Per-entity configuration: the non-removable scope (<see cref="IEntityAIToolBuilder{T}.Where(System.Linq.Expressions.Expression{Func{T, bool}})"/>),
    /// capabilities, hidden fields, descriptions and page cap. With no <c>Allow*</c> call the entity is read-only.
    /// </param>
    /// <param name="name">
    /// Slug used as the tool-name prefix (<c>{name}_query</c>). Must be unique. Defaults to the CLR type name in
    /// snake case (<c>OrderLine</c> → <c>order_line</c>).
    /// </param>
    IDbContextAIToolBuilder AddEntity<T>(Action<IEntityAIToolBuilder<T>>? configure = null, string? name = null) where T : class;
}
