namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>
/// A refusal the model should read: an unknown field, a malformed filter, a write outside the scope. Its message is
/// written for the model and never carries a scope predicate, a hidden field or provider detail — which is why a host
/// (the MCP server) may surface it verbatim, while any other exception stays a generic "the tool failed".
/// </summary>
sealed class EntityAIToolException : InvalidOperationException
{
    public EntityAIToolException(string message) : base(message) { }

    public EntityAIToolException(string message, Exception inner) : base(message, inner) { }
}
