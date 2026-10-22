using Shiny.EntityFrameworkCore.AI;

namespace Shiny.EntityFrameworkCore.Mcp;

/// <summary>
/// Configures what an MCP client may reach in one <c>DbContext</c>. It <b>is</b> the
/// <see cref="IDbContextAIToolBuilder"/> — the tools an MCP client calls are the same
/// <c>Shiny.EntityFrameworkCore.AI</c> tools an <c>IChatClient</c> calls, with the same scope rules — plus the knobs
/// that only make sense when the caller is an arbitrary agent. Call the MCP knobs before <c>AddEntity</c>, which
/// returns the base builder.
/// </summary>
public interface IDbContextMcpBuilder : IDbContextAIToolBuilder
{
    /// <summary>
    /// Unlocks the write capabilities (insert/update/delete) for this context. Writes need <b>two</b> locks: the
    /// per-entity capability <i>and</i> this. An entity that asks for a write capability without it is a startup
    /// error, not a silently downgraded server.
    /// </summary>
    IDbContextMcpBuilder AllowWrites();

    /// <summary>
    /// Publishes the orientation resources — <c>efcore://entities</c>, <c>efcore://entities/{name}/schema</c>,
    /// <c>efcore://entities/{name}/sample</c> and <c>efcore://stats</c> — so a model learns the shape of the data in
    /// one round trip instead of guessing tool arguments.
    /// </summary>
    /// <param name="sampleSize">Rows <c>…/sample</c> returns, clamped to the entity's page cap.</param>
    /// <remarks>
    /// Every resource that touches data runs <b>through the entity's own tools</b>, so the scope, the page cap and the
    /// hidden fields apply to a resource read exactly as they do to a tool call. <c>…/sample</c> needs the Query
    /// capability; the counts in <c>efcore://stats</c> need Count.
    /// </remarks>
    IDbContextMcpBuilder ExposeResources(int sampleSize = 3);

    /// <summary>
    /// Publishes the two prompts: <c>explain-entity</c> (what lives in an entity and how to filter it) and
    /// <c>build-filter</c> (turn a natural-language ask into a query call).
    /// </summary>
    IDbContextMcpBuilder ExposePrompts();
}
